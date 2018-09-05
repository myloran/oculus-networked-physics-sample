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
    cubeDelta = new CubeDelta[MaxCubes],
    predictionDelta = new CubeDelta[MaxCubes];

  public PacketHeader header;
  public ushort[] baselineIds = new ushort[MaxCubes];
  public int[] cubeIds = new int[MaxCubes];

  public bool[] 
    notChanged = new bool[MaxCubes],
    hasDelta = new bool[MaxCubes],
    perfectPrediction = new bool[MaxCubes],
    hasPredictionDelta = new bool[MaxCubes];

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
    var entry = buffer.entries[entryId];

    if (ReadUpdatePacket(packet, out entry.header, out entry.avatarCount, ref entry.avatarsQuantized, out entry.cubeCount, ref entry.cubeIds, ref entry.notChanged, ref entry.hasDelta, ref entry.perfectPrediction, ref entry.hasPredictionDelta, ref entry.baselineIds, ref entry.cubes, ref entry.cubeDelta, ref entry.predictionDelta)
    ) {
      for (int i = 0; i < entry.avatarCount; ++i)
        AvatarState.Unquantize(ref entry.avatarsQuantized[i], out entry.avatars[i]);

      DecodePrediction(receiveBuffer, resetId, entry.header.id, entry.cubeCount, ref entry.cubeIds, ref entry.perfectPrediction, ref entry.hasPredictionDelta, ref entry.baselineIds, ref entry.cubes, ref entry.predictionDelta);
      DecodeNotChangedAndDeltas(receiveBuffer, resetId, entry.cubeCount, ref entry.cubeIds, ref entry.notChanged, ref entry.hasDelta, ref entry.baselineIds, ref entry.cubes, ref entry.cubeDelta);
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

  public bool GetInterpolatedAvatars(ref AvatarState[] avatar, out int avatarCount, out ushort resetId) {
    avatarCount = 0;
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
      for (long i = frame + 1; (i > frame - n) && (i >= 0); i--) {
        var entry = GetEntry((uint)i);
        if (entry == null) continue;

        var avatarSampleTime = (i - initialFrame) * (1.0 / PhysicsFrameRate) + entry.header.timeOffset;
        if (time < avatarSampleTime || time > avatarSampleTime + (1.0f / PhysicsFrameRate))
          continue;

        startFrame = i;
        endFrame = i;
        startTime = avatarSampleTime;
        endTime = avatarSampleTime;
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

        var avatarSampleTime = (startFrame + 1 + i - initialFrame) * (1.0 / PhysicsFrameRate) + entry.header.timeOffset;
        if (avatarSampleTime < time) continue;

        endFrame = startFrame + 1 + i;
        endTime = avatarSampleTime + (1.0 / PhysicsFrameRate);
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

        AvatarState.Interpolate(ref a.avatars[i], ref b.avatars[j], out avatar[avatarCount], t);
        avatarCount++;
      }
    }
    resetId = a.header.resetSequence;

    return true;
  }

  bool ReadUpdateHeader(byte[] packet, out PacketHeader header) {
    Profiler.BeginSample("ReadStateUpdatePacketHeader");
    stream.Start(packet);
    var result = true;

    try {
      serializer.ReadUpdatePacketHeader(stream, out header);
    } catch (SerializeException) {
      Debug.Log("error: failed to read state update packet header");
      header.id = 0;
      header.ack = 0;
      header.ackBits = 0;
      header.frame = 0;
      header.resetSequence = 0;
      header.timeOffset = 0.0f;
      result = false;
    }
    stream.Finish();
    Profiler.EndSample();

    return result;
  }

  bool ReadUpdatePacket(byte[] packet, out PacketHeader header, out int avatarCount, ref AvatarStateQuantized[] avatarState, out int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineIds, ref CubeState[] cubeStates, ref CubeDelta[] cubeDeltas, ref CubeDelta[] predictionDeltas
  ) {
    Profiler.BeginSample("ReadStateUpdatePacket");
    stream.Start(packet);
    var result = true;

    try {
      serializer.ReadUpdatePacket(stream, out header, out avatarCount, avatarState, out cubeCount, cubeIds, notChanged, hasDelta, perfectPrediction, hasPredictionDelta, baselineIds, cubeStates, cubeDeltas, predictionDeltas);
    } catch (SerializeException) {
      Debug.Log("error: failed to read state update packet");
      header.id = 0;
      header.ack = 0;
      header.ackBits = 0;
      header.frame = 0;
      header.resetSequence = 0;
      header.timeOffset = 0.0f;
      avatarCount = 0;
      cubeCount = 0;
      result = false;
    }
    stream.Finish();
    Profiler.EndSample();

    return result;
  }

  bool DecodePrediction(DeltaBuffer buffer, ushort currentId, ushort resetId, int cubeCount, ref int[] cubeIds, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineIds, ref CubeState[] cubeStates, ref CubeDelta[] predictionDeltas
  ) {
    Profiler.BeginSample("DecodePrediction");
    var baselineCubeState = CubeState.defaults;
    var result = true;
#if !DISABLE_DELTA_ENCODING

    for (int i = 0; i < cubeCount; ++i) {
      if (!perfectPrediction[i] && !hasPredictionDelta[i]) continue;

      if (!buffer.GetCube(baselineIds[i], resetId, cubeIds[i], ref baselineCubeState)) {
        Debug.Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineIds[i] + " (perfect prediction and prediction delta)");
        result = false;
        break;
      }

      int baseline_sequence = baselineIds[i];
      int current_sequence = currentId;

      if (current_sequence < baseline_sequence)
        current_sequence += 65536;

      int baseline_position_x = baselineCubeState.positionX;
      int baseline_position_y = baselineCubeState.positionY;
      int baseline_position_z = baselineCubeState.positionZ;
      int baseline_linear_velocity_x = baselineCubeState.linearVelocityX;
      int baseline_linear_velocity_y = baselineCubeState.linearVelocityY;
      int baseline_linear_velocity_z = baselineCubeState.linearVelocityZ;
      int baseline_angular_velocity_x = baselineCubeState.angularVelocityX;
      int baseline_angular_velocity_y = baselineCubeState.angularVelocityY;
      int baseline_angular_velocity_z = baselineCubeState.angularVelocityZ;

      if (current_sequence < baseline_sequence)
        current_sequence += 65536;

      int numFrames = current_sequence - baseline_sequence;
      int predicted_position_x;
      int predicted_position_y;
      int predicted_position_z;
      int predicted_linear_velocity_x;
      int predicted_linear_velocity_y;
      int predicted_linear_velocity_z;
      int predicted_angular_velocity_x;
      int predicted_angular_velocity_y;
      int predicted_angular_velocity_z;

      Prediction.PredictBallistic(numFrames,
                                  baseline_position_x, baseline_position_y, baseline_position_z,
                                  baseline_linear_velocity_x, baseline_linear_velocity_y, baseline_linear_velocity_z,
                                  baseline_angular_velocity_x, baseline_angular_velocity_y, baseline_angular_velocity_z,
                                  out predicted_position_x, out predicted_position_y, out predicted_position_z,
                                  out predicted_linear_velocity_x, out predicted_linear_velocity_y, out predicted_linear_velocity_z,
                                  out predicted_angular_velocity_x, out predicted_angular_velocity_y, out predicted_angular_velocity_z);

      if (perfectPrediction[i]) {
#if DEBUG_DELTA_COMPRESSION
        Assert.IsTrue( predicted_position_x == cubeDelta[i].absolute_position_x );
        Assert.IsTrue( predicted_position_y == cubeDelta[i].absolute_position_y );
        Assert.IsTrue( predicted_position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION

        cubeStates[i].positionY = predicted_position_y;
        cubeStates[i].positionZ = predicted_position_z;
        cubeStates[i].linearVelocityX = predicted_linear_velocity_x;
        cubeStates[i].linearVelocityY = predicted_linear_velocity_y;
        cubeStates[i].linearVelocityZ = predicted_linear_velocity_z;
        cubeStates[i].angularVelocityX = predicted_angular_velocity_x;
        cubeStates[i].angularVelocityY = predicted_angular_velocity_y;
        cubeStates[i].angularVelocityZ = predicted_angular_velocity_z;

      } else {
        cubeStates[i].positionX = predicted_position_x + predictionDeltas[i].positionX;
        cubeStates[i].positionY = predicted_position_y + predictionDeltas[i].positionY;
        cubeStates[i].positionZ = predicted_position_z + predictionDeltas[i].positionZ;
#if DEBUG_DELTA_COMPRESSION
        Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
        Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
        Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
        cubeStates[i].linearVelocityX = predicted_linear_velocity_x + predictionDeltas[i].linearVelocityX;
        cubeStates[i].linearVelocityY = predicted_linear_velocity_y + predictionDeltas[i].linearVelocityY;
        cubeStates[i].linearVelocityZ = predicted_linear_velocity_z + predictionDeltas[i].linearVelocityZ;
        cubeStates[i].angularVelocityX = predicted_angular_velocity_x + predictionDeltas[i].angularVelocityX;
        cubeStates[i].angularVelocityY = predicted_angular_velocity_y + predictionDeltas[i].angularVelocityY;
        cubeStates[i].angularVelocityZ = predicted_angular_velocity_z + predictionDeltas[i].angularVelocityZ;
      }
    }
#endif // #if !DISABLE_DELTA_COMPRESSION
    Profiler.EndSample();

    return result;
  }

  bool DecodeNotChangedAndDeltas(DeltaBuffer buffer, ushort resetId, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineIds, ref CubeState[] cubeStates, ref CubeDelta[] cubeDeltas
  ) {
    Profiler.BeginSample("DecodeNotChangedAndDeltas");
    var result = true;
#if !DISABLE_DELTA_COMPRESSION
    var baselineCubeState = CubeState.defaults;

    for (int i = 0; i < cubeCount; ++i) {
      if (notChanged[i]) {
        if (buffer.GetCube(baselineIds[i], resetId, cubeIds[i], ref baselineCubeState)) {
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
        cubeStates[i] = baselineCubeState;

      } else if (hasDelta[i]) {
        if (buffer.GetCube(baselineIds[i], resetId, cubeIds[i], ref baselineCubeState)) {
          Debug.Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineIds[i] + " (delta)");
          result = false;
          break;
        }

        cubeStates[i].positionX = baselineCubeState.positionX + cubeDeltas[i].positionX;
        cubeStates[i].positionY = baselineCubeState.positionY + cubeDeltas[i].positionY;
        cubeStates[i].positionZ = baselineCubeState.positionZ + cubeDeltas[i].positionZ;
#if DEBUG_DELTA_COMPRESSION
        Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
        Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
        Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
        cubeStates[i].linearVelocityX = baselineCubeState.linearVelocityX + cubeDeltas[i].linearVelocityX;
        cubeStates[i].linearVelocityY = baselineCubeState.linearVelocityY + cubeDeltas[i].linearVelocityY;
        cubeStates[i].linearVelocityZ = baselineCubeState.linearVelocityZ + cubeDeltas[i].linearVelocityZ;
        cubeStates[i].angularVelocityX = baselineCubeState.angularVelocityX + cubeDeltas[i].angularVelocityX;
        cubeStates[i].angularVelocityY = baselineCubeState.angularVelocityY + cubeDeltas[i].angularVelocityY;
        cubeStates[i].angularVelocityZ = baselineCubeState.angularVelocityZ + cubeDeltas[i].angularVelocityZ;
      }
    }
#endif // #if !DISABLE_DELTA_COMPRESSION

    return result;
  }
}