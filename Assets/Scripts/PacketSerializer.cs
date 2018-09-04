/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using UnityEngine;
using UnityEngine.Assertions;
using Network;
using static Network.Util;
using static PacketSerializer.PacketType;
using static Constants;

public class PacketSerializer : Network.Serializer {
  public enum PacketType {
    ServerInfo = 1,                     // information about players connected to the server. broadcast from server -> clients whenever a player joins or leaves the game.
    StateUpdate = 0,                    // most recent state of the world, delta encoded relative to most recent state per-object acked by the client. sent 90 times per-second.
  };

  public void WriteClientsPacket(WriteStream w, bool[] clientConnected, ulong[] clientUserId, string[] clientUserName) {
    w.WriteBits((byte)ServerInfo, 8);

    for (int i = 0; i < MaxClients; ++i) {
      w.WriteBool(clientConnected[i]);
      if (!clientConnected[i]) continue;

      w.WriteBits(clientUserId[i], 64);
      w.WriteString(clientUserName[i]);
    }
  }

  public void ReadClientsPacket(ReadStream r, bool[] clientConnected, ulong[] clientUserId, string[] clientUserName) {
    byte packetType = 0;
    read_bits(r, out packetType, 8);
    Debug.Assert(packetType == (byte)ServerInfo);

    for (int i = 0; i < MaxClients; ++i) {
      read_bool(r, out clientConnected[i]);
      if (!clientConnected[i]) continue;

      read_bits(r, out clientUserId[i], 64);
      read_string(r, out clientUserName[i]);
    }
  }

  public void WriteUpdatePacket(WriteStream w, ref PacketHeader header, int numAvatarStates, AvatarStateQuantized[] avatarState, int numStateUpdates, int[] cubeIds, bool[] notChanged, bool[] hasDelta, bool[] perfectPrediction, bool[] hasPredictionDelta, ushort[] baselineSequence, CubeState[] cubeState, CubeDelta[] cubeDelta, CubeDelta[] predictionDelta
  ) {
    write_bits(w, (byte)StateUpdate, 8);
    write_bits(w, header.sequence, 16);
    write_bits(w, header.ack, 16);
    write_bits(w, header.ack_bits, 32);
    write_bits(w, header.frameNumber, 32);
    write_bits(w, header.resetSequence, 16);
    write_float(w, header.timeOffset);
    write_int(w, numAvatarStates, 0, MaxClients);

    for (int i = 0; i < numAvatarStates; ++i)
      write_avatar_state(w, ref avatarState[i]);

    write_int(w, numStateUpdates, 0, MaxStateUpdates);

    for (int i = 0; i < numStateUpdates; ++i) {
      write_int(w, cubeIds[i], 0, NumCubes - 1);
#if DEBUG_DELTA_COMPRESSION
      write_int( stream, cubeDelta[i].absolute_position_x, PositionMinimumXZ, PositionMaximumXZ );
      write_int( stream, cubeDelta[i].absolute_position_y, PositionMinimumY, PositionMaximumY );
      write_int( stream, cubeDelta[i].absolute_position_z, PositionMinimumXZ, PositionMaximumXZ );
#endif // #if DEBUG_DELTA_COMPRESSION
      write_int(w, cubeState[i].authorityId, 0, MaxAuthority - 1);
      write_bits(w, cubeState[i].authoritySequence, 16);
      write_bits(w, cubeState[i].ownershipSequence, 16);
      write_bool(w, notChanged[i]);

      if (notChanged[i]) {
        write_bits(w, baselineSequence[i], 16);
        continue;
      }

      write_bool(w, perfectPrediction[i]);

      if (perfectPrediction[i]) {
        write_bits(w, baselineSequence[i], 16);
        write_bits(w, cubeState[i].rotationLargest, 2);
        write_bits(w, cubeState[i].rotationX, RotationBits);
        write_bits(w, cubeState[i].rotationY, RotationBits);
        write_bits(w, cubeState[i].rotationZ, RotationBits);
        continue;
      }

      write_bool(w, hasPredictionDelta[i]);

      if (hasPredictionDelta[i]) {
        write_bits(w, baselineSequence[i], 16);
        write_bool(w, cubeState[i].isActive);
        write_linear_velocity_delta(w, predictionDelta[i].linearVelocityX, predictionDelta[i].linearVelocityY, predictionDelta[i].linearVelocityZ);
        write_angular_velocity_delta(w, predictionDelta[i].angularVelocityX, predictionDelta[i].angularVelocityY, predictionDelta[i].angularVelocityZ);
        write_position_delta(w, predictionDelta[i].positionX, predictionDelta[i].positionY, predictionDelta[i].positionZ);
        write_bits(w, cubeState[i].rotationLargest, 2);
        write_bits(w, cubeState[i].rotationX, RotationBits);
        write_bits(w, cubeState[i].rotationY, RotationBits);
        write_bits(w, cubeState[i].rotationZ, RotationBits);
        continue;
      }

      write_bool(w, hasDelta[i]);

      if (hasDelta[i]) {
        write_bits(w, baselineSequence[i], 16);
        write_bool(w, cubeState[i].isActive);
        write_linear_velocity_delta(w, cubeDelta[i].linearVelocityX, cubeDelta[i].linearVelocityY, cubeDelta[i].linearVelocityZ);
        write_angular_velocity_delta(w, cubeDelta[i].angularVelocityX, cubeDelta[i].angularVelocityY, cubeDelta[i].angularVelocityZ);
        write_position_delta(w, cubeDelta[i].positionX, cubeDelta[i].positionY, cubeDelta[i].positionZ);
        write_bits(w, cubeState[i].rotationLargest, 2);
        write_bits(w, cubeState[i].rotationX, RotationBits);
        write_bits(w, cubeState[i].rotationY, RotationBits);
        write_bits(w, cubeState[i].rotationZ, RotationBits);
        continue;
      } 

      write_bool(w, cubeState[i].isActive);
      write_int(w, cubeState[i].positionX, PositionMinimumXZ, PositionMaximumXZ);
      write_int(w, cubeState[i].positionY, PositionMinimumY, PositionMaximumY);
      write_int(w, cubeState[i].positionZ, PositionMinimumXZ, PositionMaximumXZ);
      write_bits(w, cubeState[i].rotationLargest, 2);
      write_bits(w, cubeState[i].rotationX, RotationBits);
      write_bits(w, cubeState[i].rotationY, RotationBits);
      write_bits(w, cubeState[i].rotationZ, RotationBits);

      if (!cubeState[i].isActive) continue;

      write_int(w, cubeState[i].linearVelocityX, LinearVelocityMinimum, LinearVelocityMaximum);
      write_int(w, cubeState[i].linearVelocityY, LinearVelocityMinimum, LinearVelocityMaximum);
      write_int(w, cubeState[i].linearVelocityZ, LinearVelocityMinimum, LinearVelocityMaximum);
      write_int(w, cubeState[i].angularVelocityX, AngularVelocityMinimum, AngularVelocityMaximum);
      write_int(w, cubeState[i].angularVelocityY, AngularVelocityMinimum, AngularVelocityMaximum);
      write_int(w, cubeState[i].angularVelocityZ, AngularVelocityMinimum, AngularVelocityMaximum);
    }
  }

  public void ReadStateUpdatePacketHeader(ReadStream r, out PacketHeader header) {
    byte packetType = 0;
    read_bits(r, out packetType, 8);
    Debug.Assert(packetType == (byte)StateUpdate);
    read_bits(r, out header.sequence, 16);
    read_bits(r, out header.ack, 16);
    read_bits(r, out header.ack_bits, 32);
    read_bits(r, out header.frameNumber, 32);
    read_bits(r, out header.resetSequence, 16);
    read_float(r, out header.timeOffset);
  }

  public void ReadUpdatePacket(ReadStream r, out PacketHeader header, out int numAvatarStates, AvatarStateQuantized[] avatarState, out int numStateUpdates, int[] cubeIds, bool[] notChanged, bool[] hasDelta, bool[] perfectPrediction, bool[] hasPredictionDelta, ushort[] baselineSequence, CubeState[] cubeState, CubeDelta[] cubeDelta, CubeDelta[] predictionDelta
  ) {
    byte packetType = 0;
    read_bits(r, out packetType, 8);
    Debug.Assert(packetType == (byte)StateUpdate);
    read_bits(r, out header.sequence, 16);
    read_bits(r, out header.ack, 16);
    read_bits(r, out header.ack_bits, 32);
    read_bits(r, out header.frameNumber, 32);
    read_bits(r, out header.resetSequence, 16);
    read_float(r, out header.timeOffset);
    read_int(r, out numAvatarStates, 0, MaxClients);

    for (int i = 0; i < numAvatarStates; ++i)
      read_avatar_state(r, out avatarState[i]);

    read_int(r, out numStateUpdates, 0, MaxStateUpdates);

    for (int i = 0; i < numStateUpdates; ++i) {
      hasDelta[i] = false;
      perfectPrediction[i] = false;
      hasPredictionDelta[i] = false;
      read_int(r, out cubeIds[i], 0, NumCubes - 1);
#if DEBUG_DELTA_COMPRESSION
      read_int( stream, out cubeDelta[i].absolute_position_x, PositionMinimumXZ, PositionMaximumXZ );
      read_int( stream, out cubeDelta[i].absolute_position_y, PositionMinimumY, PositionMaximumY );
      read_int( stream, out cubeDelta[i].absolute_position_z, PositionMinimumXZ, PositionMaximumXZ );
#endif // #if DEBUG_DELTA_COMPRESSION
      read_int(r, out cubeState[i].authorityId, 0, MaxAuthority - 1);
      read_bits(r, out cubeState[i].authoritySequence, 16);
      read_bits(r, out cubeState[i].ownershipSequence, 16);
      read_bool(r, out notChanged[i]);

      if (notChanged[i]) {
        read_bits(r, out baselineSequence[i], 16);
        continue;
      }

      read_bool(r, out perfectPrediction[i]);

      if (perfectPrediction[i]) {
        read_bits(r, out baselineSequence[i], 16);
        read_bits(r, out cubeState[i].rotationLargest, 2);
        read_bits(r, out cubeState[i].rotationX, RotationBits);
        read_bits(r, out cubeState[i].rotationY, RotationBits);
        read_bits(r, out cubeState[i].rotationZ, RotationBits);
        cubeState[i].isActive = true;
        continue;
      }

      read_bool(r, out hasPredictionDelta[i]);

      if (hasPredictionDelta[i]) {
        read_bits(r, out baselineSequence[i], 16);
        read_bool(r, out cubeState[i].isActive);
        read_linear_velocity_delta(r, out predictionDelta[i].linearVelocityX, out predictionDelta[i].linearVelocityY, out predictionDelta[i].linearVelocityZ);
        read_angular_velocity_delta(r, out predictionDelta[i].angularVelocityX, out predictionDelta[i].angularVelocityY, out predictionDelta[i].angularVelocityZ);
        read_position_delta(r, out predictionDelta[i].positionX, out predictionDelta[i].positionY, out predictionDelta[i].positionZ);
        read_bits(r, out cubeState[i].rotationLargest, 2);
        read_bits(r, out cubeState[i].rotationX, RotationBits);
        read_bits(r, out cubeState[i].rotationY, RotationBits);
        read_bits(r, out cubeState[i].rotationZ, RotationBits);
        continue;
      }

      read_bool(r, out hasDelta[i]);

      if (hasDelta[i]) {
        read_bits(r, out baselineSequence[i], 16);
        read_bool(r, out cubeState[i].isActive);
        read_linear_velocity_delta(r, out cubeDelta[i].linearVelocityX, out cubeDelta[i].linearVelocityY, out cubeDelta[i].linearVelocityZ);
        read_angular_velocity_delta(r, out cubeDelta[i].angularVelocityX, out cubeDelta[i].angularVelocityY, out cubeDelta[i].angularVelocityZ);
        read_position_delta(r, out cubeDelta[i].positionX, out cubeDelta[i].positionY, out cubeDelta[i].positionZ);
        read_bits(r, out cubeState[i].rotationLargest, 2);
        read_bits(r, out cubeState[i].rotationX, RotationBits);
        read_bits(r, out cubeState[i].rotationY, RotationBits);
        read_bits(r, out cubeState[i].rotationZ, RotationBits);
        continue;
      }

      read_bool(r, out cubeState[i].isActive);
      read_int(r, out cubeState[i].positionX, PositionMinimumXZ, PositionMaximumXZ);
      read_int(r, out cubeState[i].positionY, PositionMinimumY, PositionMaximumY);
      read_int(r, out cubeState[i].positionZ, PositionMinimumXZ, PositionMaximumXZ);
      read_bits(r, out cubeState[i].rotationLargest, 2);
      read_bits(r, out cubeState[i].rotationX, RotationBits);
      read_bits(r, out cubeState[i].rotationY, RotationBits);
      read_bits(r, out cubeState[i].rotationZ, RotationBits);

      if (cubeState[i].isActive) {
        read_int(r, out cubeState[i].linearVelocityX, LinearVelocityMinimum, LinearVelocityMaximum);
        read_int(r, out cubeState[i].linearVelocityY, LinearVelocityMinimum, LinearVelocityMaximum);
        read_int(r, out cubeState[i].linearVelocityZ, LinearVelocityMinimum, LinearVelocityMaximum);
        read_int(r, out cubeState[i].angularVelocityX, AngularVelocityMinimum, AngularVelocityMaximum);
        read_int(r, out cubeState[i].angularVelocityY, AngularVelocityMinimum, AngularVelocityMaximum);
        read_int(r, out cubeState[i].angularVelocityZ, AngularVelocityMinimum, AngularVelocityMaximum);
        continue;
      } 
      
      cubeState[i].linearVelocityX = 0;
      cubeState[i].linearVelocityY = 0;
      cubeState[i].linearVelocityZ = 0;
      cubeState[i].angularVelocityX = 0;
      cubeState[i].angularVelocityY = 0;
      cubeState[i].angularVelocityZ = 0;
    }
  }

  void write_position_delta(WriteStream w, int delta_x, int delta_y, int delta_z) {
    Assert.IsTrue(delta_x >= -PositionDeltaMax);
    Assert.IsTrue(delta_x <= +PositionDeltaMax);
    Assert.IsTrue(delta_y >= -PositionDeltaMax);
    Assert.IsTrue(delta_y <= +PositionDeltaMax);
    Assert.IsTrue(delta_z >= -PositionDeltaMax);
    Assert.IsTrue(delta_z <= +PositionDeltaMax);
    var unsigned_x = SignedToUnsigned(delta_x);
    var unsigned_y = SignedToUnsigned(delta_y);
    var unsigned_z = SignedToUnsigned(delta_z);
    var small_x = unsigned_x <= PositionDeltaSmallThreshold;
    var small_y = unsigned_y <= PositionDeltaSmallThreshold;
    var small_z = unsigned_z <= PositionDeltaSmallThreshold;
    var all_small = small_x && small_y && small_z;
    write_bool(w, all_small);

    if (all_small) {
      write_bits(w, unsigned_x, PositionDeltaSmallBits);
      write_bits(w, unsigned_y, PositionDeltaSmallBits);
      write_bits(w, unsigned_z, PositionDeltaSmallBits);
      return;
    }

    write_bool(w, small_x);

    if (small_x) write_bits(w, unsigned_x, PositionDeltaSmallBits);
    else {
      unsigned_x -= PositionDeltaSmallThreshold;
      var medium_x = unsigned_x < PositionDeltaMediumThreshold;
      write_bool(w, medium_x);

      if (medium_x)
        write_bits(w, unsigned_x, PositionDeltaMediumBits); else
        write_int(w, delta_x, -PositionDeltaMax, +PositionDeltaMax);
    }

    write_bool(w, small_y);

    if (small_y) write_bits(w, unsigned_y, PositionDeltaSmallBits);
    else {
      unsigned_y -= PositionDeltaSmallThreshold;
      var medium_y = unsigned_y < PositionDeltaMediumThreshold;
      write_bool(w, medium_y);

      if (medium_y)
        write_bits(w, unsigned_y, PositionDeltaMediumBits); else
        write_int(w, delta_y, -PositionDeltaMax, +PositionDeltaMax);
    }

    write_bool(w, small_z);

    if (small_z) write_bits(w, unsigned_z, PositionDeltaSmallBits);
    else {
      unsigned_z -= PositionDeltaSmallThreshold;
      var medium_z = unsigned_z < PositionDeltaMediumThreshold;
      write_bool(w, medium_z);

      if (medium_z)
        write_bits(w, unsigned_z, PositionDeltaMediumBits); else
        write_int(w, delta_z, -PositionDeltaMax, +PositionDeltaMax);
    }
  }

  void read_position_delta(ReadStream r, out int delta_x, out int delta_y, out int delta_z) {
    bool all_small;
    read_bool(r, out all_small);
    uint unsigned_x;
    uint unsigned_y;
    uint unsigned_z;

    if (all_small) {
      read_bits(r, out unsigned_x, PositionDeltaSmallBits);
      read_bits(r, out unsigned_y, PositionDeltaSmallBits);
      read_bits(r, out unsigned_z, PositionDeltaSmallBits);
      delta_x = UnsignedToSigned(unsigned_x);
      delta_y = UnsignedToSigned(unsigned_y);
      delta_z = UnsignedToSigned(unsigned_z);
      return;
    }

    bool small_x;
    read_bool(r, out small_x);

    if (small_x) {
      read_bits(r, out unsigned_x, PositionDeltaSmallBits);
      delta_x = UnsignedToSigned(unsigned_x);
    } else {
      bool medium_x;
      read_bool(r, out medium_x);

      if (medium_x) {
        read_bits(r, out unsigned_x, PositionDeltaMediumBits);
        delta_x = UnsignedToSigned(unsigned_x + PositionDeltaSmallThreshold);
      } 
      else read_int(r, out delta_x, -PositionDeltaMax, +PositionDeltaMax);
    }

    bool small_y;
    read_bool(r, out small_y);

    if (small_y) {
      read_bits(r, out unsigned_y, PositionDeltaSmallBits);
      delta_y = UnsignedToSigned(unsigned_y);
    } else {
      bool medium_y;
      read_bool(r, out medium_y);

      if (medium_y) {
        read_bits(r, out unsigned_y, PositionDeltaMediumBits);
        delta_y = UnsignedToSigned(unsigned_y + PositionDeltaSmallThreshold);
      } 
      else read_int(r, out delta_y, -PositionDeltaMax, +PositionDeltaMax);
    }

    bool small_z;
    read_bool(r, out small_z);

    if (small_z) {
      read_bits(r, out unsigned_z, PositionDeltaSmallBits);
      delta_z = UnsignedToSigned(unsigned_z);
    } else {
      bool medium_z;
      read_bool(r, out medium_z);

      if (medium_z) {
        read_bits(r, out unsigned_z, PositionDeltaMediumBits);
        delta_z = UnsignedToSigned(unsigned_z + PositionDeltaSmallThreshold);
      } 
      else read_int(r, out delta_z, -PositionDeltaMax, +PositionDeltaMax);
    }
  }

  void write_linear_velocity_delta(WriteStream w, int delta_x, int delta_y, int delta_z) {
    Assert.IsTrue(delta_x >= -LinearVelocityDeltaMax);
    Assert.IsTrue(delta_x <= +LinearVelocityDeltaMax);
    Assert.IsTrue(delta_y >= -LinearVelocityDeltaMax);
    Assert.IsTrue(delta_y <= +LinearVelocityDeltaMax);
    Assert.IsTrue(delta_z >= -LinearVelocityDeltaMax);
    Assert.IsTrue(delta_z <= +LinearVelocityDeltaMax);
    var unsigned_x = SignedToUnsigned(delta_x);
    var unsigned_y = SignedToUnsigned(delta_y);
    var unsigned_z = SignedToUnsigned(delta_z);
    var small_x = unsigned_x <= LinearVelocityDeltaSmallThreshold;
    var small_y = unsigned_y <= LinearVelocityDeltaSmallThreshold;
    var small_z = unsigned_z <= LinearVelocityDeltaSmallThreshold;
    var all_small = small_x && small_y && small_z;
    write_bool(w, all_small);

    if (all_small) {
      write_bits(w, unsigned_x, LinearVelocityDeltaSmallBits);
      write_bits(w, unsigned_y, LinearVelocityDeltaSmallBits);
      write_bits(w, unsigned_z, LinearVelocityDeltaSmallBits);
      return;
    }

    write_bool(w, small_x);

    if (small_x) write_bits(w, unsigned_x, LinearVelocityDeltaSmallBits); 
    else {
      unsigned_x -= LinearVelocityDeltaSmallThreshold;
      var medium_x = unsigned_x < LinearVelocityDeltaMediumThreshold;
      write_bool(w, medium_x);

      if (medium_x)
        write_bits(w, unsigned_x, LinearVelocityDeltaMediumBits); else
        write_int(w, delta_x, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }

    write_bool(w, small_y);

    if (small_y) write_bits(w, unsigned_y, LinearVelocityDeltaSmallBits);
    else {
      unsigned_y -= LinearVelocityDeltaSmallThreshold;
      var medium_y = unsigned_y < LinearVelocityDeltaMediumThreshold;
      write_bool(w, medium_y);

      if (medium_y)
        write_bits(w, unsigned_y, LinearVelocityDeltaMediumBits); else
        write_int(w, delta_y, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }

    write_bool(w, small_z);

    if (small_z) write_bits(w, unsigned_z, LinearVelocityDeltaSmallBits);
    else {
      unsigned_z -= LinearVelocityDeltaSmallThreshold;
      var medium_z = unsigned_z < LinearVelocityDeltaMediumThreshold;
      write_bool(w, medium_z);

      if (medium_z)
        write_bits(w, unsigned_z, LinearVelocityDeltaMediumBits); else
        write_int(w, delta_z, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }
  }

  void read_linear_velocity_delta(ReadStream r, out int delta_x, out int delta_y, out int delta_z) {
    bool all_small;
    read_bool(r, out all_small);
    uint unsigned_x;
    uint unsigned_y;
    uint unsigned_z;

    if (all_small) {
      read_bits(r, out unsigned_x, LinearVelocityDeltaSmallBits);
      read_bits(r, out unsigned_y, LinearVelocityDeltaSmallBits);
      read_bits(r, out unsigned_z, LinearVelocityDeltaSmallBits);

      delta_x = UnsignedToSigned(unsigned_x);
      delta_y = UnsignedToSigned(unsigned_y);
      delta_z = UnsignedToSigned(unsigned_z);
      return;
    }

    bool small_x;
    read_bool(r, out small_x);

    if (small_x) {
      read_bits(r, out unsigned_x, LinearVelocityDeltaSmallBits);
      delta_x = UnsignedToSigned(unsigned_x);
    } else {
      bool medium_x;
      read_bool(r, out medium_x);

      if (medium_x) {
        read_bits(r, out unsigned_x, LinearVelocityDeltaMediumBits);
        delta_x = UnsignedToSigned(unsigned_x + LinearVelocityDeltaSmallThreshold);
      } 
      else read_int(r, out delta_x, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }

    bool small_y;
    read_bool(r, out small_y);

    if (small_y) {
      read_bits(r, out unsigned_y, LinearVelocityDeltaSmallBits);
      delta_y = UnsignedToSigned(unsigned_y);
    } else {
      bool medium_y;
      read_bool(r, out medium_y);

      if (medium_y) {
        read_bits(r, out unsigned_y, LinearVelocityDeltaMediumBits);
        delta_y = UnsignedToSigned(unsigned_y + LinearVelocityDeltaSmallThreshold);
      } 
      else read_int(r, out delta_y, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }

    bool small_z;
    read_bool(r, out small_z);

    if (small_z) {
      read_bits(r, out unsigned_z, LinearVelocityDeltaSmallBits);
      delta_z = UnsignedToSigned(unsigned_z);
    } else {
      bool medium_z;
      read_bool(r, out medium_z);

      if (medium_z) {
        read_bits(r, out unsigned_z, LinearVelocityDeltaMediumBits);
        delta_z = UnsignedToSigned(unsigned_z + LinearVelocityDeltaSmallThreshold);
      } 
      else read_int(r, out delta_z, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }
  }

  void write_angular_velocity_delta(WriteStream w, int delta_x, int delta_y, int delta_z) {
    Assert.IsTrue(delta_x >= -AngularVelocityDeltaMax);
    Assert.IsTrue(delta_x <= +AngularVelocityDeltaMax);
    Assert.IsTrue(delta_y >= -AngularVelocityDeltaMax);
    Assert.IsTrue(delta_y <= +AngularVelocityDeltaMax);
    Assert.IsTrue(delta_z >= -AngularVelocityDeltaMax);
    Assert.IsTrue(delta_z <= +AngularVelocityDeltaMax);
    var unsigned_x = SignedToUnsigned(delta_x);
    var unsigned_y = SignedToUnsigned(delta_y);
    var unsigned_z = SignedToUnsigned(delta_z);
    var small_x = unsigned_x <= AngularVelocityDeltaSmallThreshold;
    var small_y = unsigned_y <= AngularVelocityDeltaSmallThreshold;
    var small_z = unsigned_z <= AngularVelocityDeltaSmallThreshold;
    var all_small = small_x && small_y && small_z;
    write_bool(w, all_small);

    if (all_small) {
      write_bits(w, unsigned_x, AngularVelocityDeltaSmallBits);
      write_bits(w, unsigned_y, AngularVelocityDeltaSmallBits);
      write_bits(w, unsigned_z, AngularVelocityDeltaSmallBits);
      return;
    }

    write_bool(w, small_x);

    if (small_x) write_bits(w, unsigned_x, AngularVelocityDeltaSmallBits);
    else {
      unsigned_x -= AngularVelocityDeltaSmallThreshold;
      var medium_x = unsigned_x < AngularVelocityDeltaMediumThreshold;
      write_bool(w, medium_x);

      if (medium_x)
        write_bits(w, unsigned_x, AngularVelocityDeltaMediumBits); else
        write_int(w, delta_x, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }

    write_bool(w, small_y);

    if (small_y) write_bits(w, unsigned_y, AngularVelocityDeltaSmallBits);
    else {
      unsigned_y -= AngularVelocityDeltaSmallThreshold;
      var medium_y = unsigned_y < AngularVelocityDeltaMediumThreshold;
      write_bool(w, medium_y);

      if (medium_y)
        write_bits(w, unsigned_y, AngularVelocityDeltaMediumBits); else
        write_int(w, delta_y, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }

    write_bool(w, small_z);

    if (small_z) write_bits(w, unsigned_z, AngularVelocityDeltaSmallBits);
    else {
      unsigned_z -= AngularVelocityDeltaSmallThreshold;
      var medium_z = unsigned_z < AngularVelocityDeltaMediumThreshold;
      write_bool(w, medium_z);

      if (medium_z)
        write_bits(w, unsigned_z, AngularVelocityDeltaMediumBits); else
        write_int(w, delta_z, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }
  }

  void read_angular_velocity_delta(ReadStream r, out int delta_x, out int delta_y, out int delta_z) {
    bool all_small;
    read_bool(r, out all_small);
    uint unsigned_x;
    uint unsigned_y;
    uint unsigned_z;

    if (all_small) {
      read_bits(r, out unsigned_x, AngularVelocityDeltaSmallBits);
      read_bits(r, out unsigned_y, AngularVelocityDeltaSmallBits);
      read_bits(r, out unsigned_z, AngularVelocityDeltaSmallBits);
      delta_x = UnsignedToSigned(unsigned_x);
      delta_y = UnsignedToSigned(unsigned_y);
      delta_z = UnsignedToSigned(unsigned_z);
      return;
    }

    bool small_x;
    read_bool(r, out small_x);

    if (small_x) {
      read_bits(r, out unsigned_x, AngularVelocityDeltaSmallBits);
      delta_x = UnsignedToSigned(unsigned_x);
    } else {
      bool medium_x;
      read_bool(r, out medium_x);

      if (medium_x) {
        read_bits(r, out unsigned_x, AngularVelocityDeltaMediumBits);
        delta_x = UnsignedToSigned(unsigned_x + AngularVelocityDeltaSmallThreshold);
      } 
      else read_int(r, out delta_x, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }

    bool small_y;
    read_bool(r, out small_y);

    if (small_y) {
      read_bits(r, out unsigned_y, AngularVelocityDeltaSmallBits);
      delta_y = UnsignedToSigned(unsigned_y);
    } else {
      bool medium_y;
      read_bool(r, out medium_y);

      if (medium_y) {
        read_bits(r, out unsigned_y, AngularVelocityDeltaMediumBits);
        delta_y = UnsignedToSigned(unsigned_y + AngularVelocityDeltaSmallThreshold);
      } 
      else read_int(r, out delta_y, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }

    bool small_z;
    read_bool(r, out small_z);

    if (small_z) {
      read_bits(r, out unsigned_z, AngularVelocityDeltaSmallBits);
      delta_z = UnsignedToSigned(unsigned_z);
    } else {
      bool medium_z;
      read_bool(r, out medium_z);

      if (medium_z) {
        read_bits(r, out unsigned_z, AngularVelocityDeltaMediumBits);
        delta_z = UnsignedToSigned(unsigned_z + AngularVelocityDeltaSmallThreshold);
      } 
      else read_int(r, out delta_z, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }
  }

  void write_avatar_state(WriteStream w, ref AvatarStateQuantized s) {
    write_int(w, s.clientId, 0, MaxClients - 1);
    write_int(w, s.headPositionX, PositionMinimumXZ, PositionMaximumXZ);
    write_int(w, s.headPositionY, PositionMinimumY, PositionMaximumY);
    write_int(w, s.headPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    write_bits(w, s.headRotationLargest, 2);
    write_bits(w, s.headRotationX, RotationBits);
    write_bits(w, s.headRotationY, RotationBits);
    write_bits(w, s.headRotationZ, RotationBits);
    write_int(w, s.leftHandPositionX, PositionMinimumXZ, PositionMaximumXZ);
    write_int(w, s.leftHandPositionY, PositionMinimumY, PositionMaximumY);
    write_int(w, s.leftHandPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    write_bits(w, s.leftHandRotationLargest, 2);
    write_bits(w, s.leftHandRotationX, RotationBits);
    write_bits(w, s.leftHandRotationY, RotationBits);
    write_bits(w, s.leftHandRotationZ, RotationBits);
    write_int(w, s.leftHandGripTrigger, TriggerMinimum, TriggerMaximum);
    write_int(w, s.leftHandIdTrigger, TriggerMinimum, TriggerMaximum);
    write_bool(w, s.isLeftHandPointing);
    write_bool(w, s.areLeftHandThumbsUp);
    write_bool(w, s.isLeftHandHoldingCube);

    if (s.isLeftHandHoldingCube) {
      write_int(w, s.leftHandCubeId, 0, NumCubes - 1);
      write_bits(w, s.leftHandAuthoritySequence, 16);
      write_bits(w, s.leftHandOwnershipSequence, 16);
      write_int(w, s.leftHandCubeLocalPositionX, LocalPositionMinimum, LocalPositionMaximum);
      write_int(w, s.leftHandCubeLocalPositionY, LocalPositionMinimum, LocalPositionMaximum);
      write_int(w, s.leftHandCubeLocalPositionZ, LocalPositionMinimum, LocalPositionMaximum);
      write_bits(w, s.leftHandCubeLocalRotationLargest, 2);
      write_bits(w, s.leftHandCubeLocalRotationX, RotationBits);
      write_bits(w, s.leftHandCubeLocalRotationY, RotationBits);
      write_bits(w, s.leftHandCubeLocalRotationZ, RotationBits);
    }

    write_int(w, s.rightHandPositionX, PositionMinimumXZ, PositionMaximumXZ);
    write_int(w, s.rightHandPositionY, PositionMinimumY, PositionMaximumY);
    write_int(w, s.rightHandPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    write_bits(w, s.rightHandRotationLargest, 2);
    write_bits(w, s.rightHandRotationX, RotationBits);
    write_bits(w, s.rightHandRotationY, RotationBits);
    write_bits(w, s.rightHandRotationZ, RotationBits);
    write_int(w, s.rightHandGripTrigger, TriggerMinimum, TriggerMaximum);
    write_int(w, s.rightHandIndexTrigger, TriggerMinimum, TriggerMaximum);
    write_bool(w, s.isRightHandPointing);
    write_bool(w, s.areRightHandThumbsUp);
    write_bool(w, s.isRightHandHoldingCube);

    if (s.isRightHandHoldingCube) {
      write_int(w, s.rightHandCubeId, 0, NumCubes - 1);
      write_bits(w, s.rightHandAuthoritySequence, 16);
      write_bits(w, s.rightHandOwnershipSequence, 16);
      write_int(w, s.rightHandCubeLocalPositionX, LocalPositionMinimum, LocalPositionMaximum);
      write_int(w, s.rightHandCubeLocalPositionY, LocalPositionMinimum, LocalPositionMaximum);
      write_int(w, s.rightHandCubeLocalPositionZ, LocalPositionMinimum, LocalPositionMaximum);
      write_bits(w, s.rightHandCubeLocalRotationLargest, 2);
      write_bits(w, s.rightHandCubeLocalRotationX, RotationBits);
      write_bits(w, s.rightHandCubeLocalRotationY, RotationBits);
      write_bits(w, s.rightHandCubeLocalRotationZ, RotationBits);
    }
    write_int(w, s.voiceAmplitude, VoiceMinimum, VoiceMaximum);
  }

  void read_avatar_state(ReadStream s, out AvatarStateQuantized a) {
    read_int(s, out a.clientId, 0, MaxClients - 1);
    read_int(s, out a.headPositionX, PositionMinimumXZ, PositionMaximumXZ);
    read_int(s, out a.headPositionY, PositionMinimumY, PositionMaximumY);
    read_int(s, out a.headPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    read_bits(s, out a.headRotationLargest, 2);
    read_bits(s, out a.headRotationX, RotationBits);
    read_bits(s, out a.headRotationY, RotationBits);
    read_bits(s, out a.headRotationZ, RotationBits);
    read_int(s, out a.leftHandPositionX, PositionMinimumXZ, PositionMaximumXZ);
    read_int(s, out a.leftHandPositionY, PositionMinimumY, PositionMaximumY);
    read_int(s, out a.leftHandPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    read_bits(s, out a.leftHandRotationLargest, 2);
    read_bits(s, out a.leftHandRotationX, RotationBits);
    read_bits(s, out a.leftHandRotationY, RotationBits);
    read_bits(s, out a.leftHandRotationZ, RotationBits);
    read_int(s, out a.leftHandGripTrigger, TriggerMinimum, TriggerMaximum);
    read_int(s, out a.leftHandIdTrigger, TriggerMinimum, TriggerMaximum);
    read_bool(s, out a.isLeftHandPointing);
    read_bool(s, out a.areLeftHandThumbsUp);
    read_bool(s, out a.isLeftHandHoldingCube);

    if (a.isLeftHandHoldingCube) {
      read_int(s, out a.leftHandCubeId, 0, NumCubes - 1);
      read_bits(s, out a.leftHandAuthoritySequence, 16);
      read_bits(s, out a.leftHandOwnershipSequence, 16);

      read_int(s, out a.leftHandCubeLocalPositionX, LocalPositionMinimum, LocalPositionMaximum);
      read_int(s, out a.leftHandCubeLocalPositionY, LocalPositionMinimum, LocalPositionMaximum);
      read_int(s, out a.leftHandCubeLocalPositionZ, LocalPositionMinimum, LocalPositionMaximum);

      read_bits(s, out a.leftHandCubeLocalRotationLargest, 2);
      read_bits(s, out a.leftHandCubeLocalRotationX, RotationBits);
      read_bits(s, out a.leftHandCubeLocalRotationY, RotationBits);
      read_bits(s, out a.leftHandCubeLocalRotationZ, RotationBits);
    } else {
      a.leftHandCubeId = 0;
      a.leftHandAuthoritySequence = 0;
      a.leftHandOwnershipSequence = 0;
      a.leftHandCubeLocalPositionX = 0;
      a.leftHandCubeLocalPositionY = 0;
      a.leftHandCubeLocalPositionZ = 0;
      a.leftHandCubeLocalRotationLargest = 0;
      a.leftHandCubeLocalRotationX = 0;
      a.leftHandCubeLocalRotationY = 0;
      a.leftHandCubeLocalRotationZ = 0;
    }

    read_int(s, out a.rightHandPositionX, PositionMinimumXZ, PositionMaximumXZ);
    read_int(s, out a.rightHandPositionY, PositionMinimumY, PositionMaximumY);
    read_int(s, out a.rightHandPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    read_bits(s, out a.rightHandRotationLargest, 2);
    read_bits(s, out a.rightHandRotationX, RotationBits);
    read_bits(s, out a.rightHandRotationY, RotationBits);
    read_bits(s, out a.rightHandRotationZ, RotationBits);
    read_int(s, out a.rightHandGripTrigger, TriggerMinimum, TriggerMaximum);
    read_int(s, out a.rightHandIndexTrigger, TriggerMinimum, TriggerMaximum);
    read_bool(s, out a.isRightHandPointing);
    read_bool(s, out a.areRightHandThumbsUp);
    read_bool(s, out a.isRightHandHoldingCube);

    if (a.isRightHandHoldingCube) {
      read_int(s, out a.rightHandCubeId, 0, NumCubes - 1);
      read_bits(s, out a.rightHandAuthoritySequence, 16);
      read_bits(s, out a.rightHandOwnershipSequence, 16);
      read_int(s, out a.rightHandCubeLocalPositionX, LocalPositionMinimum, LocalPositionMaximum);
      read_int(s, out a.rightHandCubeLocalPositionY, LocalPositionMinimum, LocalPositionMaximum);
      read_int(s, out a.rightHandCubeLocalPositionZ, LocalPositionMinimum, LocalPositionMaximum);
      read_bits(s, out a.rightHandCubeLocalRotationLargest, 2);
      read_bits(s, out a.rightHandCubeLocalRotationX, RotationBits);
      read_bits(s, out a.rightHandCubeLocalRotationY, RotationBits);
      read_bits(s, out a.rightHandCubeLocalRotationZ, RotationBits);
    } else {
      a.rightHandCubeId = 0;
      a.rightHandAuthoritySequence = 0;
      a.rightHandOwnershipSequence = 0;
      a.rightHandCubeLocalPositionX = 0;
      a.rightHandCubeLocalPositionY = 0;
      a.rightHandCubeLocalPositionZ = 0;
      a.rightHandCubeLocalRotationLargest = 0;
      a.rightHandCubeLocalRotationX = 0;
      a.rightHandCubeLocalRotationY = 0;
      a.rightHandCubeLocalRotationZ = 0;
    }
    read_int(s, out a.voiceAmplitude, VoiceMinimum, VoiceMaximum);
  }
}