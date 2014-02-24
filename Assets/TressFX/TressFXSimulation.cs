﻿using UnityEngine;
using System.Collections.Generic;
using System;
using System.Linq;

/// <summary>
/// This class is responsible for simulating the hair behaviour.
/// It will use ComputeShaders in order to do physics calculations on the GPU
/// </summary>
[RequireComponent(typeof(TressFX))]
public class TressFXSimulation : MonoBehaviour
{
	public ComputeShader HairSimulationShader;
	private TressFX master;

	/// <summary>
	/// Holds the time the compute shader needed to simulate in milliseconds.
	/// </summary>
	[HideInInspector]
	public float computationTime;

	// Configuration
	public float stiffnessForGlobalShapeMatching = 0.8f;
	public float globalShapeMatchingEffectiveRange = 0.5f;
	public float damping = 0.5f;

	// Kernel ID's
	private int IntegrationAndGlobalShapeConstraintsKernelId;
	private int LocalShapeConstraintsKernelId;
	private int LengthConstraintsAndWindKernelId;
	private int CollisionAndTangentsKernelId;
	private int SkipSimulationKernelId;

	// Buffers
	private ComputeBuffer colliderBuffer;
	private ComputeBuffer hairLengthsBuffer;
	private ComputeBuffer globalRotationBuffer;
	private ComputeBuffer localRotationBuffer;
	private ComputeBuffer referenceBuffer;
	private ComputeBuffer verticeOffsetBuffer;
	private ComputeBuffer debug;

	private ComputeBuffer hairStrandVerticeNums;

	/// <summary>
	/// This loads the kernel ids from the compute buffer and also sets it's TressFX master.
	/// </summary>
	public void Initialize(TressFXCapsuleCollider headCollider, float[] hairRestLengths, Vector3[] referenceVectors, int[] verticesOffsets)
	{
		this.master = this.GetComponent<TressFX>();
		if (this.master == null)
		{
			Debug.LogError ("TressFXSimulation doesnt have a master (TressFX)!");
		}

		// Initialize compute buffer
		this.IntegrationAndGlobalShapeConstraintsKernelId = this.HairSimulationShader.FindKernel("IntegrationAndGlobalShapeConstraints");
		this.LocalShapeConstraintsKernelId = this.HairSimulationShader.FindKernel("LocalShapeConstraints");
		this.CollisionAndTangentsKernelId = this.HairSimulationShader.FindKernel("CollisionAndTangents");
		this.LengthConstraintsAndWindKernelId = this.HairSimulationShader.FindKernel("LengthConstraintsAndWind");
		this.SkipSimulationKernelId = this.HairSimulationShader.FindKernel ("SkipSimulateHair");

		// Initialize collision buffer
		/*this.colliderBuffer = new ComputeBuffer(1, 32);
		this.colliderBuffer.SetData(new TressFXCapsuleCollider[] { headCollider });*/

		// Set length buffer
		this.hairLengthsBuffer = new ComputeBuffer(this.master.vertexCount,4);
		this.hairLengthsBuffer.SetData(hairRestLengths);

		// Set rotation buffers
		this.globalRotationBuffer = new ComputeBuffer(this.master.vertexCount, 16);
		this.localRotationBuffer = new ComputeBuffer(this.master.vertexCount, 16);
		Quaternion[] rotations = new Quaternion[this.master.vertexCount];

		// Fill with identity quaternions
		for (int i = 0; i < rotations.Length; i++)
			rotations[i] = Quaternion.identity;

		this.globalRotationBuffer.SetData(rotations);
		this.localRotationBuffer.SetData(rotations);

		// Set reference buffers
		this.referenceBuffer = new ComputeBuffer(this.master.vertexCount, 12);
		this.referenceBuffer.SetData (referenceVectors);

		// Set offset buffer
		this.verticeOffsetBuffer = new ComputeBuffer(this.master.strandCount, 4);
		this.verticeOffsetBuffer.SetData (verticesOffsets);
		
		this.debug = new ComputeBuffer(this.master.vertexCount, 12);
		
		this.HairSimulationShader.SetFloats("g_ModelPrevInvTransformForHead", this.MatrixToFloatArray(Matrix4x4.identity.inverse));
	}

	/// <summary>
	/// This functions dispatches the compute shader functions to simulate the hair behaviour
	/// </summary>
	public void Update()
	{
		long ticks = DateTime.Now.Ticks;

		this.SetResources();
		this.DispatchKernels();

		int[] debugVectors = new int[this.master.vertexCount*3];
		this.debug.GetData(debugVectors);
		
		this.computationTime = ((float) (DateTime.Now.Ticks - ticks) / 10.0f) / 1000.0f;
		
		// Set last inverse matrix
		this.HairSimulationShader.SetFloats("g_ModelPrevInvTransformForHead", this.MatrixToFloatArray(this.transform.localToWorldMatrix.inverse));
	}

	/// <summary>
	/// Sets the buffers and config values to all kernels in the compute shader.
	/// </summary>
	private void SetResources()
	{
		// Set delta time
		this.HairSimulationShader.SetFloat ("g_TimeStep", Time.deltaTime);
		this.HairSimulationShader.SetInt ("numStrands", this.master.strandCount);

		// Set matrices
		this.SetMatrices();

		// Set model rotation quaternion
		this.HairSimulationShader.SetFloats ("g_ModelRotateForHead", this.QuaternionToFloatArray(this.transform.rotation));

		// Set vertice offset buffer
		this.HairSimulationShader.SetBuffer (this.IntegrationAndGlobalShapeConstraintsKernelId, "g_HairVerticesOffsetsSRV", this.verticeOffsetBuffer);
		this.HairSimulationShader.SetBuffer (this.LocalShapeConstraintsKernelId, "g_HairVerticesOffsetsSRV", this.verticeOffsetBuffer);
		this.HairSimulationShader.SetBuffer (this.LengthConstraintsAndWindKernelId, "g_HairVerticesOffsetsSRV", this.verticeOffsetBuffer);
		this.HairSimulationShader.SetBuffer (this.CollisionAndTangentsKernelId, "g_HairVerticesOffsetsSRV", this.verticeOffsetBuffer);

		// Set rotation buffers
		this.HairSimulationShader.SetBuffer (this.LocalShapeConstraintsKernelId, "g_GlobalRotations", this.globalRotationBuffer);
		this.HairSimulationShader.SetBuffer (this.LocalShapeConstraintsKernelId, "g_LocalRotations", this.localRotationBuffer);
		
		// Set reference position buffers
		this.HairSimulationShader.SetBuffer(this.LocalShapeConstraintsKernelId, "g_HairRefVecsInLocalFrame", this.referenceBuffer);

		// Set rest lengths buffer
		this.HairSimulationShader.SetBuffer(this.LengthConstraintsAndWindKernelId, "g_HairRestLengthSRV", this.hairLengthsBuffer);

		// Set debug buffer
		this.HairSimulationShader.SetBuffer(this.LocalShapeConstraintsKernelId, "debug", this.debug);


		// Set vertex position buffers to skip simulate kernel
		this.SetVerticeInfoBuffers(this.SkipSimulationKernelId);
		this.SetVerticeInfoBuffers(this.IntegrationAndGlobalShapeConstraintsKernelId);
		this.SetVerticeInfoBuffers(this.LocalShapeConstraintsKernelId);
		this.SetVerticeInfoBuffers(this.LengthConstraintsAndWindKernelId);
		this.SetVerticeInfoBuffers(this.CollisionAndTangentsKernelId);
	}

	/// <summary>
	/// Dispatchs the compute shader kernels.
	/// </summary>
	private void DispatchKernels()
	{
		// this.HairSimulationShader.Dispatch(this.SkipSimulationKernelId, this.master.vertexCount, 1, 1);
		this.HairSimulationShader.Dispatch(this.IntegrationAndGlobalShapeConstraintsKernelId, this.master.strandCount / 2, 1, 1);
		this.HairSimulationShader.Dispatch(this.LocalShapeConstraintsKernelId, Mathf.CeilToInt((float) this.master.strandCount / 64.0f), 1, 1);
		// this.HairSimulationShader.Dispatch(this.LengthConstraintsAndWindKernelId, this.master.strandCount / 2, 1, 1);
		this.HairSimulationShader.Dispatch(this.CollisionAndTangentsKernelId, this.master.strandCount / 2, 1, 1);
	}

	/// <summary>
	/// Sets the matrices needed by the compute shader.
	/// </summary>
	private void SetMatrices()
	{
		Matrix4x4 HeadModelMatrix = this.transform.localToWorldMatrix;

		// this.HairSimulationShader.SetFloats("InverseHeadModelMatrix", this.MatrixToFloatArray(HeadModelMatrix.inverse));
		this.HairSimulationShader.SetFloats("g_ModelTransformForHead", this.MatrixToFloatArray(HeadModelMatrix));
	}

	/// <summary>
	/// Convertes a Matrix4x4 to a float array.
	/// </summary>
	/// <returns>The to float array.</returns>
	/// <param name="matrix">Matrix.</param>
	private float[] MatrixToFloatArray(Matrix4x4 matrix)
	{
		return new float[] 
		{
			matrix.m00, matrix.m01, matrix.m02, matrix.m03,
			matrix.m10, matrix.m11, matrix.m12, matrix.m13,
			matrix.m20, matrix.m21, matrix.m22, matrix.m23,
			matrix.m30, matrix.m31, matrix.m32, matrix.m33
		};
	}

	private float[] QuaternionToFloatArray(Quaternion quaternion)
	{
		return new float[]
		{
			quaternion.x,
			quaternion.y,
			quaternion.z,
			quaternion.w
		};
	}

	/// <summary>
	/// Sets the strand info buffers to a kernel with the given id
	/// </summary>
	private void SetVerticeInfoBuffers(int kernelId)
	{
		this.HairSimulationShader.SetBuffer(kernelId, "g_InitialHairPositions", this.master.InitialVertexPositionBuffer);
		this.HairSimulationShader.SetBuffer(kernelId, "g_HairVertexPositions", this.master.VertexPositionBuffer);
		this.HairSimulationShader.SetBuffer(kernelId, "g_HairVertexPositionsPrev", this.master.LastVertexPositionBuffer);
	}
}
