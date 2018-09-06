/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using Network;
using UnityEngine.Assertions;
using UnityEngine.Profiling;
using static Constants;

public class DeltaBuffer {
  struct PriorityCubes {
    public CubeState[] states;

    public int[] 
      priorityIds,
      cubeIds;

    public int count;
    public ushort resetId;
  };

  SequenceBuffer<PriorityCubes> buffer;

  public DeltaBuffer(int size) {
    buffer = new SequenceBuffer<PriorityCubes>(size);

    for (int i = 0; i < buffer.size; ++i) {
      buffer.entries[i].resetId = 0;
      buffer.entries[i].count = 0;
      buffer.entries[i].priorityIds = new int[MaxCubes];
      buffer.entries[i].cubeIds = new int[MaxCubes];
      buffer.entries[i].states = new CubeState[MaxCubes];
    }
    Reset();
  }

  public void Reset() {
    Profiler.BeginSample("DeltaBuffer.Reset");
    buffer.Reset();

    for (int i = 0; i < buffer.size; ++i) {
      buffer.entries[i].resetId = 0;
      buffer.entries[i].count = 0;
    }
    Profiler.EndSample();
  }

  public bool AddPacket(ushort packetId, ushort resetId) {
    int id = buffer.Insert(packetId);
    if (id == -1) return false;

    buffer.entries[id].resetId = resetId;
    buffer.entries[id].count = 0;

    for (int i = 0; i < MaxCubes; ++i)
      buffer.entries[id].priorityIds[i] = -1;

    return true;
  }

  public bool AddCube(ushort packetId, int cubeId, ref CubeState state) {
    int id = buffer.Get(packetId);
    if (id == -1) return false;

    int priorityId = buffer.entries[id].count;
    Assert.IsTrue(priorityId < MaxCubes);
    buffer.entries[id].priorityIds[cubeId] = priorityId;
    buffer.entries[id].cubeIds[priorityId] = cubeId;
    buffer.entries[id].states[priorityId] = state;
    buffer.entries[id].count++;

    return true;
  }

  public bool GetCube(ushort packetId, ushort resetId, int cubeId, ref CubeState state) {
    int id = buffer.Get(packetId);
    if (id == -1) return false;
    if (buffer.entries[id].resetId != resetId) return false;
    if (buffer.entries[id].count == 0) return false;

    int priorityId = buffer.entries[id].priorityIds[cubeId];
    if (priorityId == -1) return false;

    state = buffer.entries[id].states[priorityId];
    return true;
  }

  public bool GetPacketCubes(ushort packetId, ushort resetId, out int count, out int[] cubeIds, out CubeState[] states) {
    int id = buffer.Get(packetId);

    if (id == -1 || buffer.entries[id].resetId != resetId) {
      count = 0;
      cubeIds = null;
      states = null;
      return false;
    }

    count = buffer.entries[id].count;
    cubeIds = buffer.entries[id].cubeIds;
    states = buffer.entries[id].states;

    return true;
  }
}