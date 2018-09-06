/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using System;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Assertions;
using Network;
using static Constants;

public class JitterBufferEntry {
  public AvatarStateQuantized[] avatarsQuantized = new AvatarStateQuantized[MaxClients];
  public AvatarState[] avatars = new AvatarState[MaxClients];
  public CubeState[] cubes = new CubeState[MaxCubes];

  public CubeDelta[]
    deltas = new CubeDelta[MaxCubes],
    predictions = new CubeDelta[MaxCubes];

  public PacketHeader header;
  public ushort[] baselineIds = new ushort[MaxCubes];
  public int[] cubeIds = new int[MaxCubes];

  public bool[] 
    notChanged = new bool[MaxCubes],
    hasDelta = new bool[MaxCubes],
    hasPerfectPrediction = new bool[MaxCubes],
    hasPrediction = new bool[MaxCubes];

  public int 
    avatarCount = 0,
    cubeCount = 0;
}

public class JitterBuffer {
  SequenceBuffer32<JitterBufferEntry> buffer = new SequenceBuffer32<JitterBufferEntry>(JitterBufferSize);
  PacketSerializer serializer = new PacketSerializer();
  ReadStream stream = new ReadStream();

  double 
    time,
    startTime,
    endTime;

  long 
    initialFrame,
    startFrame,
    endFrame;

  bool isInterpolating;

  public JitterBuffer() { Reset(); }

  public void Reset() {
    time = -1.0;
    initialFrame = 0;
    isInterpolating = false;
    startFrame = 0;
    startTime = 0.0;
    endFrame = 0;
    endTime = 0.0;
    buffer.Reset();
  }

  public bool AddUpdatePacket(byte[] packet, DeltaBuffer receiveBuffer, ushort resetId, out long frame) {
    PacketHeader header;
    ReadUpdateHeader(packet, out header);
    frame = header.frame;
    int entryId = buffer.Insert(header.frame);
    if (entryId < 0) return false;

    var result = true;
    Profiler.BeginSample("ProcessStateUpdatePacket");
    var e = buffer.entries[entryId];

    if (ReadUpdatePacket(packet, out e.header, out e.avatarCount, ref e.avatarsQuantized, out e.cubeCount, ref e.cubeIds, ref e.notChanged, ref e.hasDelta, ref e.hasPerfectPrediction, ref e.hasPrediction, ref e.baselineIds, ref e.cubes, ref e.deltas, ref e.predictions)
    ) {
      for (int i = 0; i < e.avatarCount; ++i)
        AvatarState.Unquantize(ref e.avatarsQuantized[i], out e.avatars[i]);

      DecodePrediction(receiveBuffer, resetId, e.header.id, e.cubeCount, ref e.cubeIds, ref e.hasPerfectPrediction, ref e.hasPrediction, ref e.baselineIds, ref e.cubes, ref e.predictions);
      DecodeNotChangedAndDeltas(receiveBuffer, resetId, e.cubeCount, ref e.cubeIds, ref e.notChanged, ref e.hasDelta, ref e.baselineIds, ref e.cubes, ref e.deltas);
    } else {
      buffer.Remove(header.frame);
      result = false;
    }
    Profiler.EndSample();

    return result;
  }

  public JitterBufferEntry GetEntry(uint frame) {
    int id = buffer.Find(frame);
    if (id == -1) return null;

    return buffer.entries[id];
  }

  public void Start(long initialFrame) {
    time = 0.0;
    this.initialFrame = initialFrame;
    isInterpolating = false;
  }

  public void AdvanceTime(float deltaTime) {
    Assert.IsTrue(deltaTime >= 0.0f);
    if (time < 0) return;

    time += deltaTime;
  }

  static T Clamp<T>(T value, T min, T max) where T : IComparable<T> {
    if (value.CompareTo(max) > 0)
      return max;

    else  if (value.CompareTo(min) < 0)
      return min;

    return value;
  }

  public bool GetInterpolatedAvatars(ref AvatarState[] avatar, out int count, out ushort resetId) {
    count = 0;
    resetId = 0;    
    var frame = (long)Math.Floor(initialFrame + time * PhysicsFrameRate);
    if (frame < 0.0) return false; //if interpolation frame is negative, it's too early to display anything

    const int n = 16;

    if (isInterpolating && frame - startFrame > n)
      isInterpolating = false; //if we are interpolating but the interpolation start frame is too old, go back to the not interpolating state, so we can find a new start point.

    //if not interpolating, attempt to find an interpolation start point. 
    //if start point exists, go into interpolating mode and set end point to start point
    //so we can reuse code below to find a suitable end point on first time through.
    //if no interpolation start point is found, return.
    if (!isInterpolating) {
      for (var i = frame+1; i > frame-n && i >= 0; i--) {
        var entry = GetEntry((uint)i);
        if (entry == null) continue;

        var sampleTime = (i - initialFrame) * (1.0 / PhysicsFrameRate) + entry.header.timeOffset;
        if (time < sampleTime || time > sampleTime + (1.0f / PhysicsFrameRate))
          continue;

        startFrame = i;
        endFrame = i;
        startTime = sampleTime;
        endTime = sampleTime;
        isInterpolating = true;
      }
    }
    if (!isInterpolating) return false;

    Assert.IsTrue(time >= startTime);
    //if current time is >= end time, we need to start a new interpolation
    //from the previous end time to the next sample that exists up to n samples ahead.
    if (time >= endTime) {
      startFrame = endFrame;
      startTime = endTime;

      for (int i = 0; i < n; ++i) {
        var entry = GetEntry((uint)(startFrame + 1 + i));
        if (entry == null) continue;

        var sampleTime = (startFrame + 1 + i - initialFrame) * (1.0 / PhysicsFrameRate) + entry.header.timeOffset;
        if (sampleTime < time) continue;

        endFrame = startFrame + 1 + i;
        endTime = sampleTime + (1.0 / PhysicsFrameRate);
        break;
      }
    }    
    if (time > endTime) return false; //if current time is still > end time, we couldn't start a new interpolation so return.
    //we are in a valid interpolation, calculate t by looking at current time 
    //relative to interpolation start/end times and perform the interpolation.
    var t = (float)Clamp((time - startTime) / (endTime - startTime), 0.0, 1.0);
    var a = GetEntry((uint)(startFrame));
    var b = GetEntry((uint)(endFrame));

    for (int i = 0; i < a.avatarCount; ++i) {
      for (int j = 0; j < b.avatarCount; ++j) {
        if (a.avatars[i].clientId != b.avatars[j].clientId) continue;

        AvatarState.Interpolate(ref a.avatars[i], ref b.avatars[j], out avatar[count], t);
        count++;
      }
    }
    resetId = a.header.resetId;

    return true;
  }

  bool ReadUpdateHeader(byte[] packet, out PacketHeader h) {
    Profiler.BeginSample("ReadStateUpdatePacketHeader");
    stream.Start(packet);
    var result = true;

    try {
      serializer.ReadUpdatePacketHeader(stream, out h);
    } catch (SerializeException) {
      Debug.Log("error: failed to read state update packet header");
      h.id = 0;
      h.ack = 0;
      h.ackBits = 0;
      h.frame = 0;
      h.resetId = 0;
      h.timeOffset = 0.0f;
      result = false;
    }
    stream.Finish();
    Profiler.EndSample();

    return result;
  }

  bool ReadUpdatePacket(byte[] packet, out PacketHeader header, out int avatarCount, ref AvatarStateQuantized[] avatars, out int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] hasPerfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineIds, ref CubeState[] cubes, ref CubeDelta[] deltas, ref CubeDelta[] predictions
  ) {
    Profiler.BeginSample("ReadStateUpdatePacket");
    stream.Start(packet);
    var result = true;

    try {
      serializer.ReadUpdatePacket(stream, out header, out avatarCount, avatars, out cubeCount, cubeIds, notChanged, hasDelta, hasPerfectPrediction, hasPredictionDelta, baselineIds, cubes, deltas, predictions);
    } catch (SerializeException) {
      Debug.Log("error: failed to read state update packet");
      header.id = 0;
      header.ack = 0;
      header.ackBits = 0;
      header.frame = 0;
      header.resetId = 0;
      header.timeOffset = 0.0f;
      avatarCount = 0;
      cubeCount = 0;
      result = false;
    }
    stream.Finish();
    Profiler.EndSample();

    return result;
  }

  bool DecodePrediction(DeltaBuffer buffer, ushort currentId, ushort resetId, int cubeCount, ref int[] cubeIds, ref bool[] hasPerfectPrediction, ref bool[] hasPrediction, ref ushort[] baselineIds, ref CubeState[] states, ref CubeDelta[] predictions
  ) {
    Profiler.BeginSample("DecodePrediction");
    var baseline = CubeState.defaults;
    var result = true;
#if !DISABLE_DELTA_ENCODING

    for (int i = 0; i < cubeCount; ++i) {
      if (!hasPerfectPrediction[i] && !hasPrediction[i]) continue;

      if (!buffer.GetCube(baselineIds[i], resetId, cubeIds[i], ref baseline)) {
        Debug.Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineIds[i] + " (perfect prediction and prediction delta)");
        result = false;
        break;
      }

      int id = currentId;

      if (id < baselineIds[i])
        id += 65536;

      if (id < baselineIds[i])
        id += 65536;

      int positionX;
      int positionY;
      int positionZ;
      int linearVelocityX;
      int linearVelocityY;
      int linearVelocityZ;
      int angularVelocityX;
      int angularVelocityY;
      int angularVelocityZ;

      Prediction.PredictBallistic(id - baselineIds[i],
        baseline.positionX, baseline.positionY, baseline.positionZ,
        baseline.linearVelocityX, baseline.linearVelocityY, baseline.linearVelocityZ,
        baseline.angularVelocityX, baseline.angularVelocityY, baseline.angularVelocityZ,
        out positionX, out positionY, out positionZ,
        out linearVelocityX, out linearVelocityY, out linearVelocityZ,
        out angularVelocityX, out angularVelocityY, out angularVelocityZ);

      if (hasPerfectPrediction[i]) {
#if DEBUG_DELTA_COMPRESSION
        Assert.IsTrue( predicted_position_x == cubeDelta[i].absolute_position_x );
        Assert.IsTrue( predicted_position_y == cubeDelta[i].absolute_position_y );
        Assert.IsTrue( predicted_position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
        states[i].positionY = positionY;
        states[i].positionZ = positionZ;
        states[i].linearVelocityX = linearVelocityX;
        states[i].linearVelocityY = linearVelocityY;
        states[i].linearVelocityZ = linearVelocityZ;
        states[i].angularVelocityX = angularVelocityX;
        states[i].angularVelocityY = angularVelocityY;
        states[i].angularVelocityZ = angularVelocityZ;
        continue;
      }

      states[i].positionX = positionX + predictions[i].positionX;
      states[i].positionY = positionY + predictions[i].positionY;
      states[i].positionZ = positionZ + predictions[i].positionZ;
#if DEBUG_DELTA_COMPRESSION
      Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
      Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
      Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
      states[i].linearVelocityX = linearVelocityX + predictions[i].linearVelocityX;
      states[i].linearVelocityY = linearVelocityY + predictions[i].linearVelocityY;
      states[i].linearVelocityZ = linearVelocityZ + predictions[i].linearVelocityZ;
      states[i].angularVelocityX = angularVelocityX + predictions[i].angularVelocityX;
      states[i].angularVelocityY = angularVelocityY + predictions[i].angularVelocityY;
      states[i].angularVelocityZ = angularVelocityZ + predictions[i].angularVelocityZ;
    }
#endif // #if !DISABLE_DELTA_COMPRESSION
    Profiler.EndSample();

    return result;
  }

  bool DecodeNotChangedAndDeltas(DeltaBuffer buffer, ushort resetId, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineIds, ref CubeState[] cubes, ref CubeDelta[] deltas
  ) {
    Profiler.BeginSample("DecodeNotChangedAndDeltas");
    var result = true;
#if !DISABLE_DELTA_COMPRESSION
    var baseline = CubeState.defaults;

    for (int i = 0; i < cubeCount; ++i) {
      if (notChanged[i]) {
        if (buffer.GetCube(baselineIds[i], resetId, cubeIds[i], ref baseline)) {
          Debug.Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineIds[i] + " (not changed)");
          result = false;
          break;
        }
#if DEBUG_DELTA_COMPRESSION
        if ( baselineCubeState.position_x != cubeDelta[i].absolute_position_x )
        {
            Debug.Log( "expected " + cubeDelta[i].absolute_position_x + ", got " + baselineCubeState.position_x );
        }
        Assert.IsTrue( baselineCubeState.position_x == cubeDelta[i].absolute_position_x );
        Assert.IsTrue( baselineCubeState.position_y == cubeDelta[i].absolute_position_y );
        Assert.IsTrue( baselineCubeState.position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
        cubes[i] = baseline;

      } else if (hasDelta[i]) {
        if (buffer.GetCube(baselineIds[i], resetId, cubeIds[i], ref baseline)) {
          Debug.Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineIds[i] + " (delta)");
          result = false;
          break;
        }

        cubes[i].positionX = baseline.positionX + deltas[i].positionX;
        cubes[i].positionY = baseline.positionY + deltas[i].positionY;
        cubes[i].positionZ = baseline.positionZ + deltas[i].positionZ;
#if DEBUG_DELTA_COMPRESSION
        Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
        Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
        Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
        cubes[i].linearVelocityX = baseline.linearVelocityX + deltas[i].linearVelocityX;
        cubes[i].linearVelocityY = baseline.linearVelocityY + deltas[i].linearVelocityY;
        cubes[i].linearVelocityZ = baseline.linearVelocityZ + deltas[i].linearVelocityZ;
        cubes[i].angularVelocityX = baseline.angularVelocityX + deltas[i].angularVelocityX;
        cubes[i].angularVelocityY = baseline.angularVelocityY + deltas[i].angularVelocityY;
        cubes[i].angularVelocityZ = baseline.angularVelocityZ + deltas[i].angularVelocityZ;
      }
    }
#endif // #if !DISABLE_DELTA_COMPRESSION

    return result;
  }
}