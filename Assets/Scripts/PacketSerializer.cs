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

  public void WriteClientsPacket(WriteStream w, bool[] areConnected, ulong[] userIds, string[] userNames) {
    w.Bits((byte)ServerInfo, 8);

    for (int i = 0; i < MaxClients; ++i) {
      w.Bool(areConnected[i]);
      if (!areConnected[i]) continue;

      w.Bits(userIds[i], 64);
      w.String(userNames[i]);
    }
  }

  public void ReadClientsPacket(ReadStream r, bool[] areConnected, ulong[] userIds, string[] userNames) {
    byte packetType = 0;
    r.Bits(out packetType, 8);
    Debug.Assert(packetType == (byte)ServerInfo);

    for (int i = 0; i < MaxClients; ++i) {
      r.Bool(out areConnected[i]);
      if (!areConnected[i]) continue;

      r.Bits(out userIds[i], 64);
      r.String(out userNames[i]);
    }
  }

  public void WriteUpdatePacket(WriteStream w, ref PacketHeader header, int avatarCount, AvatarStateQuantized[] avatarState, int cubeCount, int[] cubeIds, bool[] notChanged, bool[] hasDelta, bool[] arePerfectPrediction, bool[] hasPredictionDelta, ushort[] baselineSequence, CubeState[] cubeState, CubeDelta[] cubeDelta, CubeDelta[] predictionDelta
  ) {
    w.Bits((byte)StateUpdate, 8);
    w.Bits(header.sequence, 16);
    w.Bits(header.ack, 16);
    w.Bits(header.ackBits, 32);
    w.Bits(header.frame, 32);
    w.Bits(header.resetSequence, 16);
    w.Float(header.timeOffset);
    w.Int(avatarCount, 0, MaxClients);

    for (int i = 0; i < avatarCount; ++i)
      WriteAvatar(w, ref avatarState[i]);

    w.Int(cubeCount, 0, MaxStateUpdates);

    for (int i = 0; i < cubeCount; ++i) {
      w.Int(cubeIds[i], 0, NumCubes - 1);
#if DEBUG_DELTA_COMPRESSION
      write_int( stream, cubeDelta[i].absolute_position_x, PositionMinimumXZ, PositionMaximumXZ );
      write_int( stream, cubeDelta[i].absolute_position_y, PositionMinimumY, PositionMaximumY );
      write_int( stream, cubeDelta[i].absolute_position_z, PositionMinimumXZ, PositionMaximumXZ );
#endif // #if DEBUG_DELTA_COMPRESSION
      w.Int(cubeState[i].authorityId, 0, MaxAuthority - 1);
      w.Bits(cubeState[i].authoritySequence, 16);
      w.Bits(cubeState[i].ownershipSequence, 16);
      w.Bool(notChanged[i]);

      if (notChanged[i]) {
        w.Bits(baselineSequence[i], 16);
        continue;
      }

      w.Bool(arePerfectPrediction[i]);

      if (arePerfectPrediction[i]) {
        w.Bits(baselineSequence[i], 16);
        w.Bits(cubeState[i].rotationLargest, 2);
        w.Bits(cubeState[i].rotationX, RotationBits);
        w.Bits(cubeState[i].rotationY, RotationBits);
        w.Bits(cubeState[i].rotationZ, RotationBits);
        continue;
      }

      w.Bool(hasPredictionDelta[i]);

      if (hasPredictionDelta[i]) {
        w.Bits(baselineSequence[i], 16);
        w.Bool(cubeState[i].isActive);
        WriteLinearVelocityDelta(w, predictionDelta[i].linearVelocityX, predictionDelta[i].linearVelocityY, predictionDelta[i].linearVelocityZ);
        WriteAngularVelocityDelta(w, predictionDelta[i].angularVelocityX, predictionDelta[i].angularVelocityY, predictionDelta[i].angularVelocityZ);
        WritePositionDelta(w, predictionDelta[i].positionX, predictionDelta[i].positionY, predictionDelta[i].positionZ);
        w.Bits(cubeState[i].rotationLargest, 2);
        w.Bits(cubeState[i].rotationX, RotationBits);
        w.Bits(cubeState[i].rotationY, RotationBits);
        w.Bits(cubeState[i].rotationZ, RotationBits);
        continue;
      }

      w.Bool(hasDelta[i]);

      if (hasDelta[i]) {
        w.Bits(baselineSequence[i], 16);
        w.Bool(cubeState[i].isActive);
        WriteLinearVelocityDelta(w, cubeDelta[i].linearVelocityX, cubeDelta[i].linearVelocityY, cubeDelta[i].linearVelocityZ);
        WriteAngularVelocityDelta(w, cubeDelta[i].angularVelocityX, cubeDelta[i].angularVelocityY, cubeDelta[i].angularVelocityZ);
        WritePositionDelta(w, cubeDelta[i].positionX, cubeDelta[i].positionY, cubeDelta[i].positionZ);
        w.Bits(cubeState[i].rotationLargest, 2);
        w.Bits(cubeState[i].rotationX, RotationBits);
        w.Bits(cubeState[i].rotationY, RotationBits);
        w.Bits(cubeState[i].rotationZ, RotationBits);
        continue;
      } 

      w.Bool(cubeState[i].isActive);
      w.Int(cubeState[i].positionX, PositionMinimumXZ, PositionMaximumXZ);
      w.Int(cubeState[i].positionY, PositionMinimumY, PositionMaximumY);
      w.Int(cubeState[i].positionZ, PositionMinimumXZ, PositionMaximumXZ);
      w.Bits(cubeState[i].rotationLargest, 2);
      w.Bits(cubeState[i].rotationX, RotationBits);
      w.Bits(cubeState[i].rotationY, RotationBits);
      w.Bits(cubeState[i].rotationZ, RotationBits);

      if (!cubeState[i].isActive) continue;

      w.Int(cubeState[i].linearVelocityX, LinearVelocityMinimum, LinearVelocityMaximum);
      w.Int(cubeState[i].linearVelocityY, LinearVelocityMinimum, LinearVelocityMaximum);
      w.Int(cubeState[i].linearVelocityZ, LinearVelocityMinimum, LinearVelocityMaximum);
      w.Int(cubeState[i].angularVelocityX, AngularVelocityMinimum, AngularVelocityMaximum);
      w.Int(cubeState[i].angularVelocityY, AngularVelocityMinimum, AngularVelocityMaximum);
      w.Int(cubeState[i].angularVelocityZ, AngularVelocityMinimum, AngularVelocityMaximum);
    }
  }

  public void ReadStateUpdatePacketHeader(ReadStream r, out PacketHeader header) {
    byte packetType = 0;
    r.Bits(out packetType, 8);
    Debug.Assert(packetType == (byte)StateUpdate);
    r.Bits(out header.sequence, 16);
    r.Bits(out header.ack, 16);
    r.Bits(out header.ackBits, 32);
    r.Bits(out header.frame, 32);
    r.Bits(out header.resetSequence, 16);
    read_float(r, out header.timeOffset);
  }

  public void ReadUpdatePacket(ReadStream r, out PacketHeader header, out int numAvatarStates, AvatarStateQuantized[] avatarState, out int numStateUpdates, int[] cubeIds, bool[] notChanged, bool[] hasDelta, bool[] perfectPrediction, bool[] hasPredictionDelta, ushort[] baselineSequence, CubeState[] cubeState, CubeDelta[] cubeDelta, CubeDelta[] predictionDelta
  ) {
    byte packetType = 0;
    r.Bits(out packetType, 8);
    Debug.Assert(packetType == (byte)StateUpdate);
    r.Bits(out header.sequence, 16);
    r.Bits(out header.ack, 16);
    r.Bits(out header.ackBits, 32);
    r.Bits(out header.frame, 32);
    r.Bits(out header.resetSequence, 16);
    r.Float(out header.timeOffset);
    r.Int(out numAvatarStates, 0, MaxClients);

    for (int i = 0; i < numAvatarStates; ++i)
      ReadAvatar(r, out avatarState[i]);

    r.Int(out numStateUpdates, 0, MaxStateUpdates);

    for (int i = 0; i < numStateUpdates; ++i) {
      hasDelta[i] = false;
      perfectPrediction[i] = false;
      hasPredictionDelta[i] = false;
      r.Int(out cubeIds[i], 0, NumCubes - 1);
#if DEBUG_DELTA_COMPRESSION
      read_int( stream, out cubeDelta[i].absolute_position_x, PositionMinimumXZ, PositionMaximumXZ );
      read_int( stream, out cubeDelta[i].absolute_position_y, PositionMinimumY, PositionMaximumY );
      read_int( stream, out cubeDelta[i].absolute_position_z, PositionMinimumXZ, PositionMaximumXZ );
#endif // #if DEBUG_DELTA_COMPRESSION
      r.Int(out cubeState[i].authorityId, 0, MaxAuthority - 1);
      r.Bits(out cubeState[i].authoritySequence, 16);
      r.Bits(out cubeState[i].ownershipSequence, 16);
      r.Bool(out notChanged[i]);

      if (notChanged[i]) {
        r.Bits(out baselineSequence[i], 16);
        continue;
      }

      r.Bool(out perfectPrediction[i]);

      if (perfectPrediction[i]) {
        r.Bits(out baselineSequence[i], 16);
        r.Bits(out cubeState[i].rotationLargest, 2);
        r.Bits(out cubeState[i].rotationX, RotationBits);
        r.Bits(out cubeState[i].rotationY, RotationBits);
        r.Bits(out cubeState[i].rotationZ, RotationBits);
        cubeState[i].isActive = true;
        continue;
      }

      r.Bool(out hasPredictionDelta[i]);

      if (hasPredictionDelta[i]) {
        r.Bits(out baselineSequence[i], 16);
        r.Bool(out cubeState[i].isActive);
        ReadLinearVelocityDelta(r, out predictionDelta[i].linearVelocityX, out predictionDelta[i].linearVelocityY, out predictionDelta[i].linearVelocityZ);
        ReadAngularVelocityDelta(r, out predictionDelta[i].angularVelocityX, out predictionDelta[i].angularVelocityY, out predictionDelta[i].angularVelocityZ);
        ReadPositionDelta(r, out predictionDelta[i].positionX, out predictionDelta[i].positionY, out predictionDelta[i].positionZ);
        r.Bits(out cubeState[i].rotationLargest, 2);
        r.Bits(out cubeState[i].rotationX, RotationBits);
        r.Bits(out cubeState[i].rotationY, RotationBits);
        r.Bits(out cubeState[i].rotationZ, RotationBits);
        continue;
      }

      r.Bool(out hasDelta[i]);

      if (hasDelta[i]) {
        r.Bits(out baselineSequence[i], 16);
        r.Bool(out cubeState[i].isActive);
        ReadLinearVelocityDelta(r, out cubeDelta[i].linearVelocityX, out cubeDelta[i].linearVelocityY, out cubeDelta[i].linearVelocityZ);
        ReadAngularVelocityDelta(r, out cubeDelta[i].angularVelocityX, out cubeDelta[i].angularVelocityY, out cubeDelta[i].angularVelocityZ);
        ReadPositionDelta(r, out cubeDelta[i].positionX, out cubeDelta[i].positionY, out cubeDelta[i].positionZ);
        r.Bits(out cubeState[i].rotationLargest, 2);
        r.Bits(out cubeState[i].rotationX, RotationBits);
        r.Bits(out cubeState[i].rotationY, RotationBits);
        r.Bits(out cubeState[i].rotationZ, RotationBits);
        continue;
      }

      r.Bool(out cubeState[i].isActive);
      r.Int(out cubeState[i].positionX, PositionMinimumXZ, PositionMaximumXZ);
      r.Int(out cubeState[i].positionY, PositionMinimumY, PositionMaximumY);
      r.Int(out cubeState[i].positionZ, PositionMinimumXZ, PositionMaximumXZ);
      r.Bits(out cubeState[i].rotationLargest, 2);
      r.Bits(out cubeState[i].rotationX, RotationBits);
      r.Bits(out cubeState[i].rotationY, RotationBits);
      r.Bits(out cubeState[i].rotationZ, RotationBits);

      if (cubeState[i].isActive) {
        r.Int(out cubeState[i].linearVelocityX, LinearVelocityMinimum, LinearVelocityMaximum);
        r.Int(out cubeState[i].linearVelocityY, LinearVelocityMinimum, LinearVelocityMaximum);
        r.Int(out cubeState[i].linearVelocityZ, LinearVelocityMinimum, LinearVelocityMaximum);
        r.Int(out cubeState[i].angularVelocityX, AngularVelocityMinimum, AngularVelocityMaximum);
        r.Int(out cubeState[i].angularVelocityY, AngularVelocityMinimum, AngularVelocityMaximum);
        r.Int(out cubeState[i].angularVelocityZ, AngularVelocityMinimum, AngularVelocityMaximum);
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

  void WritePositionDelta(WriteStream w, int deltaX, int deltaY, int deltaZ) {
    Assert.IsTrue(deltaX >= -PositionDeltaMax);
    Assert.IsTrue(deltaX <= PositionDeltaMax);
    Assert.IsTrue(deltaY >= -PositionDeltaMax);
    Assert.IsTrue(deltaY <= PositionDeltaMax);
    Assert.IsTrue(deltaZ >= -PositionDeltaMax);
    Assert.IsTrue(deltaZ <= PositionDeltaMax);
    var unsignedX = SignedToUnsigned(deltaX);
    var unsignedY = SignedToUnsigned(deltaY);
    var unsignedZ = SignedToUnsigned(deltaZ);
    var isSmallX = unsignedX <= PositionDeltaSmallThreshold;
    var isSmallY = unsignedY <= PositionDeltaSmallThreshold;
    var isSmallZ = unsignedZ <= PositionDeltaSmallThreshold;
    var isAllSmall = isSmallX && isSmallY && isSmallZ;
    w.Bool(isAllSmall);

    if (isAllSmall) {
      w.Bits(unsignedX, PositionDeltaSmallBits);
      w.Bits(unsignedY, PositionDeltaSmallBits);
      w.Bits(unsignedZ, PositionDeltaSmallBits);
      return;
    }

    w.Bool(isSmallX);

    if (isSmallX) w.Bits(unsignedX, PositionDeltaSmallBits);
    else {
      unsignedX -= PositionDeltaSmallThreshold;
      var isMediumX = unsignedX < PositionDeltaMediumThreshold;
      w.Bool(isMediumX);

      if (isMediumX)
        w.Bits(unsignedX, PositionDeltaMediumBits); else
        w.Int(deltaX, -PositionDeltaMax, +PositionDeltaMax);
    }

    w.Bool(isSmallY);

    if (isSmallY) w.Bits(unsignedY, PositionDeltaSmallBits);
    else {
      unsignedY -= PositionDeltaSmallThreshold;
      var isMediumY = unsignedY < PositionDeltaMediumThreshold;
      w.Bool(isMediumY);

      if (isMediumY)
        w.Bits(unsignedY, PositionDeltaMediumBits); else
        w.Int(deltaY, -PositionDeltaMax, +PositionDeltaMax);
    }

    w.Bool(isSmallZ);

    if (isSmallZ) w.Bits(unsignedZ, PositionDeltaSmallBits);
    else {
      unsignedZ -= PositionDeltaSmallThreshold;
      var isMediumZ = unsignedZ < PositionDeltaMediumThreshold;
      w.Bool(isMediumZ);

      if (isMediumZ)
        w.Bits(unsignedZ, PositionDeltaMediumBits); else
        w.Int(deltaZ, -PositionDeltaMax, +PositionDeltaMax);
    }
  }

  void ReadPositionDelta(ReadStream r, out int deltaX, out int deltaY, out int deltaZ) {
    bool isAllSmall;
    r.Bool(out isAllSmall);
    uint unsignedX;
    uint unsignedY;
    uint unsignedZ;

    if (isAllSmall) {
      r.Bits(out unsignedX, PositionDeltaSmallBits);
      r.Bits(out unsignedY, PositionDeltaSmallBits);
      r.Bits(out unsignedZ, PositionDeltaSmallBits);
      deltaX = UnsignedToSigned(unsignedX);
      deltaY = UnsignedToSigned(unsignedY);
      deltaZ = UnsignedToSigned(unsignedZ);
      return;
    }

    bool isSmallX;
    r.Bool(out isSmallX);

    if (isSmallX) {
      r.Bits(out unsignedX, PositionDeltaSmallBits);
      deltaX = UnsignedToSigned(unsignedX);
    } else {
      bool isMediumX;
      r.Bool(out isMediumX);

      if (isMediumX) {
        r.Bits(out unsignedX, PositionDeltaMediumBits);
        deltaX = UnsignedToSigned(unsignedX + PositionDeltaSmallThreshold);
      } 
      else r.Int(out deltaX, -PositionDeltaMax, +PositionDeltaMax);
    }

    bool isSmallY;
    r.Bool(out isSmallY);

    if (isSmallY) {
      r.Bits(out unsignedY, PositionDeltaSmallBits);
      deltaY = UnsignedToSigned(unsignedY);
    } else {
      bool isMediumY;
      r.Bool(out isMediumY);

      if (isMediumY) {
        r.Bits(out unsignedY, PositionDeltaMediumBits);
        deltaY = UnsignedToSigned(unsignedY + PositionDeltaSmallThreshold);
      } 
      else r.Int(out deltaY, -PositionDeltaMax, +PositionDeltaMax);
    }

    bool isSmallZ;
    r.Bool(out isSmallZ);

    if (isSmallZ) {
      r.Bits(out unsignedZ, PositionDeltaSmallBits);
      deltaZ = UnsignedToSigned(unsignedZ);
    } else {
      bool isMediumZ;
      r.Bool(out isMediumZ);

      if (isMediumZ) {
        r.Bits(out unsignedZ, PositionDeltaMediumBits);
        deltaZ = UnsignedToSigned(unsignedZ + PositionDeltaSmallThreshold);
      } 
      else r.Int(out deltaZ, -PositionDeltaMax, +PositionDeltaMax);
    }
  }

  void WriteLinearVelocityDelta(WriteStream w, int deltaX, int deltaY, int deltaZ) {
    Assert.IsTrue(deltaX >= -LinearVelocityDeltaMax);
    Assert.IsTrue(deltaX <= +LinearVelocityDeltaMax);
    Assert.IsTrue(deltaY >= -LinearVelocityDeltaMax);
    Assert.IsTrue(deltaY <= +LinearVelocityDeltaMax);
    Assert.IsTrue(deltaZ >= -LinearVelocityDeltaMax);
    Assert.IsTrue(deltaZ <= +LinearVelocityDeltaMax);
    var unsignedX = SignedToUnsigned(deltaX);
    var unsignedY = SignedToUnsigned(deltaY);
    var unsignedZ = SignedToUnsigned(deltaZ);
    var isSmallX = unsignedX <= LinearVelocityDeltaSmallThreshold;
    var isSmallY = unsignedY <= LinearVelocityDeltaSmallThreshold;
    var isSmallZ = unsignedZ <= LinearVelocityDeltaSmallThreshold;
    var isAllSmall = isSmallX && isSmallY && isSmallZ;
    w.Bool(isAllSmall);

    if (isAllSmall) {
      w.Bits(unsignedX, LinearVelocityDeltaSmallBits);
      w.Bits(unsignedY, LinearVelocityDeltaSmallBits);
      w.Bits(unsignedZ, LinearVelocityDeltaSmallBits);
      return;
    }

    w.Bool(isSmallX);

    if (isSmallX) w.Bits(unsignedX, LinearVelocityDeltaSmallBits); 
    else {
      unsignedX -= LinearVelocityDeltaSmallThreshold;
      var isMediumX = unsignedX < LinearVelocityDeltaMediumThreshold;
      w.Bool(isMediumX);

      if (isMediumX)
        w.Bits(unsignedX, LinearVelocityDeltaMediumBits); else
        w.Int(deltaX, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }

    w.Bool(isSmallY);

    if (isSmallY) w.Bits(unsignedY, LinearVelocityDeltaSmallBits);
    else {
      unsignedY -= LinearVelocityDeltaSmallThreshold;
      var isMediumY = unsignedY < LinearVelocityDeltaMediumThreshold;
      w.Bool(isMediumY);

      if (isMediumY)
        w.Bits(unsignedY, LinearVelocityDeltaMediumBits); else
        w.Int(deltaY, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }

    w.Bool(isSmallZ);

    if (isSmallZ) w.Bits(unsignedZ, LinearVelocityDeltaSmallBits);
    else {
      unsignedZ -= LinearVelocityDeltaSmallThreshold;
      var isMediumZ = unsignedZ < LinearVelocityDeltaMediumThreshold;
      w.Bool(isMediumZ);

      if (isMediumZ)
        w.Bits(unsignedZ, LinearVelocityDeltaMediumBits); else
        w.Int(deltaZ, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }
  }

  void ReadLinearVelocityDelta(ReadStream r, out int deltaX, out int deltaY, out int deltaZ) {
    bool isAllSmall;
    r.Bool(out isAllSmall);
    uint unsignedX;
    uint unsignedY;
    uint unsignedZ;

    if (isAllSmall) {
      r.Bits(out unsignedX, LinearVelocityDeltaSmallBits);
      r.Bits(out unsignedY, LinearVelocityDeltaSmallBits);
      r.Bits(out unsignedZ, LinearVelocityDeltaSmallBits);

      deltaX = UnsignedToSigned(unsignedX);
      deltaY = UnsignedToSigned(unsignedY);
      deltaZ = UnsignedToSigned(unsignedZ);
      return;
    }

    bool isSmallX;
    r.Bool(out isSmallX);

    if (isSmallX) {
      r.Bits(out unsignedX, LinearVelocityDeltaSmallBits);
      deltaX = UnsignedToSigned(unsignedX);
    } else {
      bool isMediumX;
      r.Bool(out isMediumX);

      if (isMediumX) {
        r.Bits(out unsignedX, LinearVelocityDeltaMediumBits);
        deltaX = UnsignedToSigned(unsignedX + LinearVelocityDeltaSmallThreshold);
      } 
      else r.Int(out deltaX, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }

    bool isSmallY;
    r.Bool(out isSmallY);

    if (isSmallY) {
      r.Bits(out unsignedY, LinearVelocityDeltaSmallBits);
      deltaY = UnsignedToSigned(unsignedY);
    } else {
      bool isMediumY;
      r.Bool(out isMediumY);

      if (isMediumY) {
        r.Bits(out unsignedY, LinearVelocityDeltaMediumBits);
        deltaY = UnsignedToSigned(unsignedY + LinearVelocityDeltaSmallThreshold);
      } 
      else r.Int(out deltaY, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }

    bool isSmallZ;
    r.Bool(out isSmallZ);

    if (isSmallZ) {
      r.Bits(out unsignedZ, LinearVelocityDeltaSmallBits);
      deltaZ = UnsignedToSigned(unsignedZ);
    } else {
      bool isMediumZ;
      r.Bool(out isMediumZ);

      if (isMediumZ) {
        r.Bits(out unsignedZ, LinearVelocityDeltaMediumBits);
        deltaZ = UnsignedToSigned(unsignedZ + LinearVelocityDeltaSmallThreshold);
      } 
      else r.Int(out deltaZ, -LinearVelocityDeltaMax, +LinearVelocityDeltaMax);
    }
  }

  void WriteAngularVelocityDelta(WriteStream w, int deltaX, int deltaY, int deltaZ) {
    Assert.IsTrue(deltaX >= -AngularVelocityDeltaMax);
    Assert.IsTrue(deltaX <= +AngularVelocityDeltaMax);
    Assert.IsTrue(deltaY >= -AngularVelocityDeltaMax);
    Assert.IsTrue(deltaY <= +AngularVelocityDeltaMax);
    Assert.IsTrue(deltaZ >= -AngularVelocityDeltaMax);
    Assert.IsTrue(deltaZ <= +AngularVelocityDeltaMax);
    var unsignedX = SignedToUnsigned(deltaX);
    var unsignedY = SignedToUnsigned(deltaY);
    var unsignedZ = SignedToUnsigned(deltaZ);
    var isSmallX = unsignedX <= AngularVelocityDeltaSmallThreshold;
    var isSmallY = unsignedY <= AngularVelocityDeltaSmallThreshold;
    var isSmallZ = unsignedZ <= AngularVelocityDeltaSmallThreshold;
    var isAllSmall = isSmallX && isSmallY && isSmallZ;
    w.Bool(isAllSmall);

    if (isAllSmall) {
      w.Bits(unsignedX, AngularVelocityDeltaSmallBits);
      w.Bits(unsignedY, AngularVelocityDeltaSmallBits);
      w.Bits(unsignedZ, AngularVelocityDeltaSmallBits);
      return;
    }

    w.Bool(isSmallX);

    if (isSmallX) w.Bits(unsignedX, AngularVelocityDeltaSmallBits);
    else {
      unsignedX -= AngularVelocityDeltaSmallThreshold;
      var isMediumX = unsignedX < AngularVelocityDeltaMediumThreshold;
      w.Bool(isMediumX);

      if (isMediumX)
        w.Bits(unsignedX, AngularVelocityDeltaMediumBits); else
        w.Int(deltaX, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }

    w.Bool(isSmallY);

    if (isSmallY) w.Bits(unsignedY, AngularVelocityDeltaSmallBits);
    else {
      unsignedY -= AngularVelocityDeltaSmallThreshold;
      var isMediumY = unsignedY < AngularVelocityDeltaMediumThreshold;
      w.Bool(isMediumY);

      if (isMediumY)
        w.Bits(unsignedY, AngularVelocityDeltaMediumBits); else
        w.Int(deltaY, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }

    w.Bool(isSmallZ);

    if (isSmallZ) w.Bits(unsignedZ, AngularVelocityDeltaSmallBits);
    else {
      unsignedZ -= AngularVelocityDeltaSmallThreshold;
      var isMediumZ = unsignedZ < AngularVelocityDeltaMediumThreshold;
      w.Bool(isMediumZ);

      if (isMediumZ)
        w.Bits(unsignedZ, AngularVelocityDeltaMediumBits); else
        w.Int(deltaZ, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }
  }

  void ReadAngularVelocityDelta(ReadStream r, out int deltaX, out int deltaY, out int deltaZ) {
    bool isAllSmall;
    r.Bool(out isAllSmall);
    uint unsignedX;
    uint unsignedY;
    uint unsignedZ;

    if (isAllSmall) {
      r.Bits(out unsignedX, AngularVelocityDeltaSmallBits);
      r.Bits(out unsignedY, AngularVelocityDeltaSmallBits);
      r.Bits(out unsignedZ, AngularVelocityDeltaSmallBits);
      deltaX = UnsignedToSigned(unsignedX);
      deltaY = UnsignedToSigned(unsignedY);
      deltaZ = UnsignedToSigned(unsignedZ);
      return;
    }

    bool isSmallX;
    r.Bool(out isSmallX);

    if (isSmallX) {
      r.Bits(out unsignedX, AngularVelocityDeltaSmallBits);
      deltaX = UnsignedToSigned(unsignedX);
    } else {
      bool isMediumX;
      r.Bool(out isMediumX);

      if (isMediumX) {
        r.Bits(out unsignedX, AngularVelocityDeltaMediumBits);
        deltaX = UnsignedToSigned(unsignedX + AngularVelocityDeltaSmallThreshold);
      } 
      else r.Int(out deltaX, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }

    bool isSmallY;
    r.Bool(out isSmallY);

    if (isSmallY) {
      r.Bits(out unsignedY, AngularVelocityDeltaSmallBits);
      deltaY = UnsignedToSigned(unsignedY);
    } else {
      bool isMediumY;
      r.Bool(out isMediumY);

      if (isMediumY) {
        r.Bits(out unsignedY, AngularVelocityDeltaMediumBits);
        deltaY = UnsignedToSigned(unsignedY + AngularVelocityDeltaSmallThreshold);
      } 
      else r.Int(out deltaY, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }

    bool isSmallZ;
    r.Bool(out isSmallZ);

    if (isSmallZ) {
      r.Bits(out unsignedZ, AngularVelocityDeltaSmallBits);
      deltaZ = UnsignedToSigned(unsignedZ);
    } else {
      bool isMediumZ;
      r.Bool(out isMediumZ);

      if (isMediumZ) {
        r.Bits(out unsignedZ, AngularVelocityDeltaMediumBits);
        deltaZ = UnsignedToSigned(unsignedZ + AngularVelocityDeltaSmallThreshold);
      } 
      else r.Int(out deltaZ, -AngularVelocityDeltaMax, +AngularVelocityDeltaMax);
    }
  }

  void WriteAvatar(WriteStream w, ref AvatarStateQuantized s) {
    w.Int(s.clientId, 0, MaxClients - 1);
    w.Int(s.headPositionX, PositionMinimumXZ, PositionMaximumXZ);
    w.Int(s.headPositionY, PositionMinimumY, PositionMaximumY);
    w.Int(s.headPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    w.Bits(s.headRotationLargest, 2);
    w.Bits(s.headRotationX, RotationBits);
    w.Bits(s.headRotationY, RotationBits);
    w.Bits(s.headRotationZ, RotationBits);
    w.Int(s.leftHandPositionX, PositionMinimumXZ, PositionMaximumXZ);
    w.Int(s.leftHandPositionY, PositionMinimumY, PositionMaximumY);
    w.Int(s.leftHandPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    w.Bits(s.leftHandRotationLargest, 2);
    w.Bits(s.leftHandRotationX, RotationBits);
    w.Bits(s.leftHandRotationY, RotationBits);
    w.Bits(s.leftHandRotationZ, RotationBits);
    w.Int(s.leftHandGripTrigger, TriggerMinimum, TriggerMaximum);
    w.Int(s.leftHandIdTrigger, TriggerMinimum, TriggerMaximum);
    w.Bool(s.isLeftHandPointing);
    w.Bool(s.areLeftHandThumbsUp);
    w.Bool(s.isLeftHandHoldingCube);

    if (s.isLeftHandHoldingCube) {
      w.Int(s.leftHandCubeId, 0, NumCubes - 1);
      w.Bits(s.leftHandAuthoritySequence, 16);
      w.Bits(s.leftHandOwnershipSequence, 16);
      w.Int(s.leftHandCubeLocalPositionX, LocalPositionMinimum, LocalPositionMaximum);
      w.Int(s.leftHandCubeLocalPositionY, LocalPositionMinimum, LocalPositionMaximum);
      w.Int(s.leftHandCubeLocalPositionZ, LocalPositionMinimum, LocalPositionMaximum);
      w.Bits(s.leftHandCubeLocalRotationLargest, 2);
      w.Bits(s.leftHandCubeLocalRotationX, RotationBits);
      w.Bits(s.leftHandCubeLocalRotationY, RotationBits);
      w.Bits(s.leftHandCubeLocalRotationZ, RotationBits);
    }

    w.Int(s.rightHandPositionX, PositionMinimumXZ, PositionMaximumXZ);
    w.Int(s.rightHandPositionY, PositionMinimumY, PositionMaximumY);
    w.Int(s.rightHandPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    w.Bits(s.rightHandRotationLargest, 2);
    w.Bits(s.rightHandRotationX, RotationBits);
    w.Bits(s.rightHandRotationY, RotationBits);
    w.Bits(s.rightHandRotationZ, RotationBits);
    w.Int(s.rightHandGripTrigger, TriggerMinimum, TriggerMaximum);
    w.Int(s.rightHandIndexTrigger, TriggerMinimum, TriggerMaximum);
    w.Bool(s.isRightHandPointing);
    w.Bool(s.areRightHandThumbsUp);
    w.Bool(s.isRightHandHoldingCube);

    if (s.isRightHandHoldingCube) {
      w.Int(s.rightHandCubeId, 0, NumCubes - 1);
      w.Bits(s.rightHandAuthoritySequence, 16);
      w.Bits(s.rightHandOwnershipSequence, 16);
      w.Int(s.rightHandCubeLocalPositionX, LocalPositionMinimum, LocalPositionMaximum);
      w.Int(s.rightHandCubeLocalPositionY, LocalPositionMinimum, LocalPositionMaximum);
      w.Int(s.rightHandCubeLocalPositionZ, LocalPositionMinimum, LocalPositionMaximum);
      w.Bits(s.rightHandCubeLocalRotationLargest, 2);
      w.Bits(s.rightHandCubeLocalRotationX, RotationBits);
      w.Bits(s.rightHandCubeLocalRotationY, RotationBits);
      w.Bits(s.rightHandCubeLocalRotationZ, RotationBits);
    }
    w.Int(s.voiceAmplitude, VoiceMinimum, VoiceMaximum);
  }

  void ReadAvatar(ReadStream s, out AvatarStateQuantized a) {
    s.Int(out a.clientId, 0, MaxClients - 1);
    s.Int(out a.headPositionX, PositionMinimumXZ, PositionMaximumXZ);
    s.Int(out a.headPositionY, PositionMinimumY, PositionMaximumY);
    s.Int(out a.headPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    s.Bits(out a.headRotationLargest, 2);
    s.Bits(out a.headRotationX, RotationBits);
    s.Bits(out a.headRotationY, RotationBits);
    s.Bits(out a.headRotationZ, RotationBits);
    s.Int(out a.leftHandPositionX, PositionMinimumXZ, PositionMaximumXZ);
    s.Int(out a.leftHandPositionY, PositionMinimumY, PositionMaximumY);
    s.Int(out a.leftHandPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    s.Bits(out a.leftHandRotationLargest, 2);
    s.Bits(out a.leftHandRotationX, RotationBits);
    s.Bits(out a.leftHandRotationY, RotationBits);
    s.Bits(out a.leftHandRotationZ, RotationBits);
    s.Int(out a.leftHandGripTrigger, TriggerMinimum, TriggerMaximum);
    s.Int(out a.leftHandIdTrigger, TriggerMinimum, TriggerMaximum);
    s.Bool(out a.isLeftHandPointing);
    s.Bool(out a.areLeftHandThumbsUp);
    s.Bool(out a.isLeftHandHoldingCube);

    if (a.isLeftHandHoldingCube) {
      s.Int(out a.leftHandCubeId, 0, NumCubes - 1);
      s.Bits(out a.leftHandAuthoritySequence, 16);
      s.Bits(out a.leftHandOwnershipSequence, 16);
      s.Int(out a.leftHandCubeLocalPositionX, LocalPositionMinimum, LocalPositionMaximum);
      s.Int(out a.leftHandCubeLocalPositionY, LocalPositionMinimum, LocalPositionMaximum);
      s.Int(out a.leftHandCubeLocalPositionZ, LocalPositionMinimum, LocalPositionMaximum);
      s.Bits(out a.leftHandCubeLocalRotationLargest, 2);
      s.Bits(out a.leftHandCubeLocalRotationX, RotationBits);
      s.Bits(out a.leftHandCubeLocalRotationY, RotationBits);
      s.Bits(out a.leftHandCubeLocalRotationZ, RotationBits);

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

    s.Int(out a.rightHandPositionX, PositionMinimumXZ, PositionMaximumXZ);
    s.Int(out a.rightHandPositionY, PositionMinimumY, PositionMaximumY);
    s.Int(out a.rightHandPositionZ, PositionMinimumXZ, PositionMaximumXZ);
    s.Bits(out a.rightHandRotationLargest, 2);
    s.Bits(out a.rightHandRotationX, RotationBits);
    s.Bits(out a.rightHandRotationY, RotationBits);
    s.Bits(out a.rightHandRotationZ, RotationBits);
    s.Int(out a.rightHandGripTrigger, TriggerMinimum, TriggerMaximum);
    s.Int(out a.rightHandIndexTrigger, TriggerMinimum, TriggerMaximum);
    s.Bool(out a.isRightHandPointing);
    s.Bool(out a.areRightHandThumbsUp);
    s.Bool(out a.isRightHandHoldingCube);

    if (a.isRightHandHoldingCube) {
      s.Int(out a.rightHandCubeId, 0, NumCubes - 1);
      s.Bits(out a.rightHandAuthoritySequence, 16);
      s.Bits(out a.rightHandOwnershipSequence, 16);
      s.Int(out a.rightHandCubeLocalPositionX, LocalPositionMinimum, LocalPositionMaximum);
      s.Int(out a.rightHandCubeLocalPositionY, LocalPositionMinimum, LocalPositionMaximum);
      s.Int(out a.rightHandCubeLocalPositionZ, LocalPositionMinimum, LocalPositionMaximum);
      s.Bits(out a.rightHandCubeLocalRotationLargest, 2);
      s.Bits(out a.rightHandCubeLocalRotationX, RotationBits);
      s.Bits(out a.rightHandCubeLocalRotationY, RotationBits);
      s.Bits(out a.rightHandCubeLocalRotationZ, RotationBits);

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
    s.Int(out a.voiceAmplitude, VoiceMinimum, VoiceMaximum);
  }
}