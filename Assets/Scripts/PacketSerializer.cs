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

public class PacketSerializer {
  public enum PacketType {
    ClientsInfo = 1,                     // information about players connected to the server. broadcast from server -> clients whenever a player joins or leaves the game.
    StateUpdate = 0,                    // most recent state of the world, delta encoded relative to most recent state per-object acked by the client. sent 90 times per-second.
  };

  public void WriteClientsPacket(WriteStream w, bool[] areConnected, ulong[] userIds, string[] userNames) {
    w.Bits((byte)ClientsInfo, 8);

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
    Debug.Assert(packetType == (byte)ClientsInfo);

    for (int i = 0; i < MaxClients; ++i) {
      r.Bool(out areConnected[i]);
      if (!areConnected[i]) continue;

      r.Bits(out userIds[i], 64);
      r.String(out userNames[i]);
    }
  }

  public void WriteUpdatePacket(WriteStream w, ref PacketHeader header, int avatarCount, AvatarStateQuantized[] avatars, int cubeCount, int[] cubeIds, bool[] notChanged, bool[] hasDelta, bool[] arePerfectPrediction, bool[] hasPredictionDelta, ushort[] baselineIds, CubeState[] cubes, CubeDelta[] deltas, CubeDelta[] predictionDeltas
  ) {
    w.Bits((byte)StateUpdate, 8);
    w.Bits(header.id, 16);
    w.Bits(header.ack, 16);
    w.Bits(header.ackBits, 32);
    w.Bits(header.frame, 32);
    w.Bits(header.resetSequence, 16);
    w.Float(header.timeOffset);
    w.Int(avatarCount, 0, MaxClients);

    for (int i = 0; i < avatarCount; ++i)
      WriteAvatar(w, ref avatars[i]);

    w.Int(cubeCount, 0, MaxStateUpdates);

    for (int i = 0; i < cubeCount; ++i) {
      w.Int(cubeIds[i], 0, MaxCubes - 1);
#if DEBUG_DELTA_COMPRESSION
      write_int( stream, cubeDelta[i].absolute_position_x, PositionMinimumXZ, PositionMaximumXZ );
      write_int( stream, cubeDelta[i].absolute_position_y, PositionMinimumY, PositionMaximumY );
      write_int( stream, cubeDelta[i].absolute_position_z, PositionMinimumXZ, PositionMaximumXZ );
#endif // #if DEBUG_DELTA_COMPRESSION
      w.Int(cubes[i].authorityId, 0, MaxAuthority - 1);
      w.Bits(cubes[i].authoritySequence, 16);
      w.Bits(cubes[i].ownershipSequence, 16);
      w.Bool(notChanged[i]);

      if (notChanged[i]) {
        w.Bits(baselineIds[i], 16);
        continue;
      }

      w.Bool(arePerfectPrediction[i]);

      if (arePerfectPrediction[i]) {
        w.Bits(baselineIds[i], 16);
        w.Bits(cubes[i].rotationLargest, 2);
        w.Bits(cubes[i].rotationX, RotationBits);
        w.Bits(cubes[i].rotationY, RotationBits);
        w.Bits(cubes[i].rotationZ, RotationBits);
        continue;
      }

      w.Bool(hasPredictionDelta[i]);

      if (hasPredictionDelta[i]) {
        w.Bits(baselineIds[i], 16);
        w.Bool(cubes[i].isActive);
        WriteLinearVelocityDelta(w, predictionDeltas[i].linearVelocityX, predictionDeltas[i].linearVelocityY, predictionDeltas[i].linearVelocityZ);
        WriteAngularVelocityDelta(w, predictionDeltas[i].angularVelocityX, predictionDeltas[i].angularVelocityY, predictionDeltas[i].angularVelocityZ);
        WritePositionDelta(w, predictionDeltas[i].positionX, predictionDeltas[i].positionY, predictionDeltas[i].positionZ);
        w.Bits(cubes[i].rotationLargest, 2);
        w.Bits(cubes[i].rotationX, RotationBits);
        w.Bits(cubes[i].rotationY, RotationBits);
        w.Bits(cubes[i].rotationZ, RotationBits);
        continue;
      }

      w.Bool(hasDelta[i]);

      if (hasDelta[i]) {
        w.Bits(baselineIds[i], 16);
        w.Bool(cubes[i].isActive);
        WriteLinearVelocityDelta(w, deltas[i].linearVelocityX, deltas[i].linearVelocityY, deltas[i].linearVelocityZ);
        WriteAngularVelocityDelta(w, deltas[i].angularVelocityX, deltas[i].angularVelocityY, deltas[i].angularVelocityZ);
        WritePositionDelta(w, deltas[i].positionX, deltas[i].positionY, deltas[i].positionZ);
        w.Bits(cubes[i].rotationLargest, 2);
        w.Bits(cubes[i].rotationX, RotationBits);
        w.Bits(cubes[i].rotationY, RotationBits);
        w.Bits(cubes[i].rotationZ, RotationBits);
        continue;
      } 

      w.Bool(cubes[i].isActive);
      w.Int(cubes[i].positionX, MinPositionXZ, MaxPositionXZ);
      w.Int(cubes[i].positionY, MinPositionY, MaxPositionY);
      w.Int(cubes[i].positionZ, MinPositionXZ, MaxPositionXZ);
      w.Bits(cubes[i].rotationLargest, 2);
      w.Bits(cubes[i].rotationX, RotationBits);
      w.Bits(cubes[i].rotationY, RotationBits);
      w.Bits(cubes[i].rotationZ, RotationBits);

      if (!cubes[i].isActive) continue;

      w.Int(cubes[i].linearVelocityX, LinearVelocityMinimum, LinearVelocityMaximum);
      w.Int(cubes[i].linearVelocityY, LinearVelocityMinimum, LinearVelocityMaximum);
      w.Int(cubes[i].linearVelocityZ, LinearVelocityMinimum, LinearVelocityMaximum);
      w.Int(cubes[i].angularVelocityX, AngularVelocityMinimum, AngularVelocityMaximum);
      w.Int(cubes[i].angularVelocityY, AngularVelocityMinimum, AngularVelocityMaximum);
      w.Int(cubes[i].angularVelocityZ, AngularVelocityMinimum, AngularVelocityMaximum);
    }
  }

  public void ReadUpdatePacketHeader(ReadStream r, out PacketHeader h) {
    byte packetType = 0;
    r.Bits(out packetType, 8);
    Debug.Assert(packetType == (byte)StateUpdate);
    r.Bits(out h.id, 16);
    r.Bits(out h.ack, 16);
    r.Bits(out h.ackBits, 32);
    r.Bits(out h.frame, 32);
    r.Bits(out h.resetSequence, 16);
    r.Float(out h.timeOffset);
  }

  public void ReadUpdatePacket(ReadStream r, out PacketHeader header, out int avatarCount, AvatarStateQuantized[] avatars, out int cubeCount, int[] cubeIds, bool[] notChanged, bool[] hasDelta, bool[] perfectPrediction, bool[] hasPredictionDelta, ushort[] baselineIds, CubeState[] cubes, CubeDelta[] deltas, CubeDelta[] predictionDeltas
  ) {
    byte packetType = 0;
    r.Bits(out packetType, 8);
    Debug.Assert(packetType == (byte)StateUpdate);
    r.Bits(out header.id, 16);
    r.Bits(out header.ack, 16);
    r.Bits(out header.ackBits, 32);
    r.Bits(out header.frame, 32);
    r.Bits(out header.resetSequence, 16);
    r.Float(out header.timeOffset);
    r.Int(out avatarCount, 0, MaxClients);

    for (int i = 0; i < avatarCount; ++i)
      ReadAvatar(r, out avatars[i]);

    r.Int(out cubeCount, 0, MaxStateUpdates);

    for (int i = 0; i < cubeCount; ++i) {
      hasDelta[i] = false;
      perfectPrediction[i] = false;
      hasPredictionDelta[i] = false;
      r.Int(out cubeIds[i], 0, MaxCubes - 1);
#if DEBUG_DELTA_COMPRESSION
      read_int( stream, out cubeDelta[i].absolute_position_x, PositionMinimumXZ, PositionMaximumXZ );
      read_int( stream, out cubeDelta[i].absolute_position_y, PositionMinimumY, PositionMaximumY );
      read_int( stream, out cubeDelta[i].absolute_position_z, PositionMinimumXZ, PositionMaximumXZ );
#endif // #if DEBUG_DELTA_COMPRESSION
      r.Int(out cubes[i].authorityId, 0, MaxAuthority - 1);
      r.Bits(out cubes[i].authoritySequence, 16);
      r.Bits(out cubes[i].ownershipSequence, 16);
      r.Bool(out notChanged[i]);

      if (notChanged[i]) {
        r.Bits(out baselineIds[i], 16);
        continue;
      }

      r.Bool(out perfectPrediction[i]);

      if (perfectPrediction[i]) {
        r.Bits(out baselineIds[i], 16);
        r.Bits(out cubes[i].rotationLargest, 2);
        r.Bits(out cubes[i].rotationX, RotationBits);
        r.Bits(out cubes[i].rotationY, RotationBits);
        r.Bits(out cubes[i].rotationZ, RotationBits);
        cubes[i].isActive = true;
        continue;
      }

      r.Bool(out hasPredictionDelta[i]);

      if (hasPredictionDelta[i]) {
        r.Bits(out baselineIds[i], 16);
        r.Bool(out cubes[i].isActive);
        ReadLinearVelocityDelta(r, out predictionDeltas[i].linearVelocityX, out predictionDeltas[i].linearVelocityY, out predictionDeltas[i].linearVelocityZ);
        ReadAngularVelocityDelta(r, out predictionDeltas[i].angularVelocityX, out predictionDeltas[i].angularVelocityY, out predictionDeltas[i].angularVelocityZ);
        ReadPositionDelta(r, out predictionDeltas[i].positionX, out predictionDeltas[i].positionY, out predictionDeltas[i].positionZ);
        r.Bits(out cubes[i].rotationLargest, 2);
        r.Bits(out cubes[i].rotationX, RotationBits);
        r.Bits(out cubes[i].rotationY, RotationBits);
        r.Bits(out cubes[i].rotationZ, RotationBits);
        continue;
      }

      r.Bool(out hasDelta[i]);

      if (hasDelta[i]) {
        r.Bits(out baselineIds[i], 16);
        r.Bool(out cubes[i].isActive);
        ReadLinearVelocityDelta(r, out deltas[i].linearVelocityX, out deltas[i].linearVelocityY, out deltas[i].linearVelocityZ);
        ReadAngularVelocityDelta(r, out deltas[i].angularVelocityX, out deltas[i].angularVelocityY, out deltas[i].angularVelocityZ);
        ReadPositionDelta(r, out deltas[i].positionX, out deltas[i].positionY, out deltas[i].positionZ);
        r.Bits(out cubes[i].rotationLargest, 2);
        r.Bits(out cubes[i].rotationX, RotationBits);
        r.Bits(out cubes[i].rotationY, RotationBits);
        r.Bits(out cubes[i].rotationZ, RotationBits);
        continue;
      }

      r.Bool(out cubes[i].isActive);
      r.Int(out cubes[i].positionX, MinPositionXZ, MaxPositionXZ);
      r.Int(out cubes[i].positionY, MinPositionY, MaxPositionY);
      r.Int(out cubes[i].positionZ, MinPositionXZ, MaxPositionXZ);
      r.Bits(out cubes[i].rotationLargest, 2);
      r.Bits(out cubes[i].rotationX, RotationBits);
      r.Bits(out cubes[i].rotationY, RotationBits);
      r.Bits(out cubes[i].rotationZ, RotationBits);

      if (cubes[i].isActive) {
        r.Int(out cubes[i].linearVelocityX, LinearVelocityMinimum, LinearVelocityMaximum);
        r.Int(out cubes[i].linearVelocityY, LinearVelocityMinimum, LinearVelocityMaximum);
        r.Int(out cubes[i].linearVelocityZ, LinearVelocityMinimum, LinearVelocityMaximum);
        r.Int(out cubes[i].angularVelocityX, AngularVelocityMinimum, AngularVelocityMaximum);
        r.Int(out cubes[i].angularVelocityY, AngularVelocityMinimum, AngularVelocityMaximum);
        r.Int(out cubes[i].angularVelocityZ, AngularVelocityMinimum, AngularVelocityMaximum);
        continue;
      } 
      
      cubes[i].linearVelocityX = 0;
      cubes[i].linearVelocityY = 0;
      cubes[i].linearVelocityZ = 0;
      cubes[i].angularVelocityX = 0;
      cubes[i].angularVelocityY = 0;
      cubes[i].angularVelocityZ = 0;
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
        w.Int(deltaX, -PositionDeltaMax, PositionDeltaMax);
    }

    w.Bool(isSmallY);

    if (isSmallY) w.Bits(unsignedY, PositionDeltaSmallBits);
    else {
      unsignedY -= PositionDeltaSmallThreshold;
      var isMediumY = unsignedY < PositionDeltaMediumThreshold;
      w.Bool(isMediumY);

      if (isMediumY)
        w.Bits(unsignedY, PositionDeltaMediumBits); else
        w.Int(deltaY, -PositionDeltaMax, PositionDeltaMax);
    }

    w.Bool(isSmallZ);

    if (isSmallZ) w.Bits(unsignedZ, PositionDeltaSmallBits);
    else {
      unsignedZ -= PositionDeltaSmallThreshold;
      var isMediumZ = unsignedZ < PositionDeltaMediumThreshold;
      w.Bool(isMediumZ);

      if (isMediumZ)
        w.Bits(unsignedZ, PositionDeltaMediumBits); else
        w.Int(deltaZ, -PositionDeltaMax, PositionDeltaMax);
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
      else r.Int(out deltaX, -PositionDeltaMax, PositionDeltaMax);
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
      else r.Int(out deltaY, -PositionDeltaMax, PositionDeltaMax);
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
      else r.Int(out deltaZ, -PositionDeltaMax, PositionDeltaMax);
    }
  }

  void WriteLinearVelocityDelta(WriteStream w, int deltaX, int deltaY, int deltaZ) {
    Assert.IsTrue(deltaX >= -LinearVelocityDeltaMax);
    Assert.IsTrue(deltaX <= LinearVelocityDeltaMax);
    Assert.IsTrue(deltaY >= -LinearVelocityDeltaMax);
    Assert.IsTrue(deltaY <= LinearVelocityDeltaMax);
    Assert.IsTrue(deltaZ >= -LinearVelocityDeltaMax);
    Assert.IsTrue(deltaZ <= LinearVelocityDeltaMax);
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
        w.Int(deltaX, -LinearVelocityDeltaMax, LinearVelocityDeltaMax);
    }

    w.Bool(isSmallY);

    if (isSmallY) w.Bits(unsignedY, LinearVelocityDeltaSmallBits);
    else {
      unsignedY -= LinearVelocityDeltaSmallThreshold;
      var isMediumY = unsignedY < LinearVelocityDeltaMediumThreshold;
      w.Bool(isMediumY);

      if (isMediumY)
        w.Bits(unsignedY, LinearVelocityDeltaMediumBits); else
        w.Int(deltaY, -LinearVelocityDeltaMax, LinearVelocityDeltaMax);
    }

    w.Bool(isSmallZ);

    if (isSmallZ) w.Bits(unsignedZ, LinearVelocityDeltaSmallBits);
    else {
      unsignedZ -= LinearVelocityDeltaSmallThreshold;
      var isMediumZ = unsignedZ < LinearVelocityDeltaMediumThreshold;
      w.Bool(isMediumZ);

      if (isMediumZ)
        w.Bits(unsignedZ, LinearVelocityDeltaMediumBits); else
        w.Int(deltaZ, -LinearVelocityDeltaMax, LinearVelocityDeltaMax);
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
      else r.Int(out deltaX, -LinearVelocityDeltaMax, LinearVelocityDeltaMax);
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
      else r.Int(out deltaY, -LinearVelocityDeltaMax, LinearVelocityDeltaMax);
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
      else r.Int(out deltaZ, -LinearVelocityDeltaMax, LinearVelocityDeltaMax);
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
        w.Int(deltaX, -AngularVelocityDeltaMax, AngularVelocityDeltaMax);
    }

    w.Bool(isSmallY);

    if (isSmallY) w.Bits(unsignedY, AngularVelocityDeltaSmallBits);
    else {
      unsignedY -= AngularVelocityDeltaSmallThreshold;
      var isMediumY = unsignedY < AngularVelocityDeltaMediumThreshold;
      w.Bool(isMediumY);

      if (isMediumY)
        w.Bits(unsignedY, AngularVelocityDeltaMediumBits); else
        w.Int(deltaY, -AngularVelocityDeltaMax, AngularVelocityDeltaMax);
    }

    w.Bool(isSmallZ);

    if (isSmallZ) w.Bits(unsignedZ, AngularVelocityDeltaSmallBits);
    else {
      unsignedZ -= AngularVelocityDeltaSmallThreshold;
      var isMediumZ = unsignedZ < AngularVelocityDeltaMediumThreshold;
      w.Bool(isMediumZ);

      if (isMediumZ)
        w.Bits(unsignedZ, AngularVelocityDeltaMediumBits); else
        w.Int(deltaZ, -AngularVelocityDeltaMax, AngularVelocityDeltaMax);
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
      else r.Int(out deltaX, -AngularVelocityDeltaMax, AngularVelocityDeltaMax);
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
      else r.Int(out deltaY, -AngularVelocityDeltaMax, AngularVelocityDeltaMax);
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
      else r.Int(out deltaZ, -AngularVelocityDeltaMax, AngularVelocityDeltaMax);
    }
  }

  void WriteAvatar(WriteStream w, ref AvatarStateQuantized s) {
    w.Int(s.clientId, 0, MaxClients - 1);
    w.Int(s.headPositionX, MinPositionXZ, MaxPositionXZ);
    w.Int(s.headPositionY, MinPositionY, MaxPositionY);
    w.Int(s.headPositionZ, MinPositionXZ, MaxPositionXZ);
    w.Bits(s.headRotationLargest, 2);
    w.Bits(s.headRotationX, RotationBits);
    w.Bits(s.headRotationY, RotationBits);
    w.Bits(s.headRotationZ, RotationBits);
    w.Int(s.leftHandPositionX, MinPositionXZ, MaxPositionXZ);
    w.Int(s.leftHandPositionY, MinPositionY, MaxPositionY);
    w.Int(s.leftHandPositionZ, MinPositionXZ, MaxPositionXZ);
    w.Bits(s.leftHandRotationLargest, 2);
    w.Bits(s.leftHandRotationX, RotationBits);
    w.Bits(s.leftHandRotationY, RotationBits);
    w.Bits(s.leftHandRotationZ, RotationBits);
    w.Int(s.leftHandGripTrigger, MinTrigger, MaxTrigger);
    w.Int(s.leftHandIdTrigger, MinTrigger, MaxTrigger);
    w.Bool(s.isLeftHandPointing);
    w.Bool(s.areLeftHandThumbsUp);
    w.Bool(s.isLeftHandHoldingCube);

    if (s.isLeftHandHoldingCube) {
      w.Int(s.leftHandCubeId, 0, MaxCubes - 1);
      w.Bits(s.leftHandAuthoritySequence, 16);
      w.Bits(s.leftHandOwnershipSequence, 16);
      w.Int(s.leftHandCubeLocalPositionX, MinLocalPosition, MaxLocalPosition);
      w.Int(s.leftHandCubeLocalPositionY, MinLocalPosition, MaxLocalPosition);
      w.Int(s.leftHandCubeLocalPositionZ, MinLocalPosition, MaxLocalPosition);
      w.Bits(s.leftHandCubeLocalRotationLargest, 2);
      w.Bits(s.leftHandCubeLocalRotationX, RotationBits);
      w.Bits(s.leftHandCubeLocalRotationY, RotationBits);
      w.Bits(s.leftHandCubeLocalRotationZ, RotationBits);
    }

    w.Int(s.rightHandPositionX, MinPositionXZ, MaxPositionXZ);
    w.Int(s.rightHandPositionY, MinPositionY, MaxPositionY);
    w.Int(s.rightHandPositionZ, MinPositionXZ, MaxPositionXZ);
    w.Bits(s.rightHandRotationLargest, 2);
    w.Bits(s.rightHandRotationX, RotationBits);
    w.Bits(s.rightHandRotationY, RotationBits);
    w.Bits(s.rightHandRotationZ, RotationBits);
    w.Int(s.rightHandGripTrigger, MinTrigger, MaxTrigger);
    w.Int(s.rightHandIndexTrigger, MinTrigger, MaxTrigger);
    w.Bool(s.isRightHandPointing);
    w.Bool(s.areRightHandThumbsUp);
    w.Bool(s.isRightHandHoldingCube);

    if (s.isRightHandHoldingCube) {
      w.Int(s.rightHandCubeId, 0, MaxCubes - 1);
      w.Bits(s.rightHandAuthoritySequence, 16);
      w.Bits(s.rightHandOwnershipSequence, 16);
      w.Int(s.rightHandCubeLocalPositionX, MinLocalPosition, MaxLocalPosition);
      w.Int(s.rightHandCubeLocalPositionY, MinLocalPosition, MaxLocalPosition);
      w.Int(s.rightHandCubeLocalPositionZ, MinLocalPosition, MaxLocalPosition);
      w.Bits(s.rightHandCubeLocalRotationLargest, 2);
      w.Bits(s.rightHandCubeLocalRotationX, RotationBits);
      w.Bits(s.rightHandCubeLocalRotationY, RotationBits);
      w.Bits(s.rightHandCubeLocalRotationZ, RotationBits);
    }
    w.Int(s.voiceAmplitude, MinVoice, MaxVoice);
  }

  void ReadAvatar(ReadStream s, out AvatarStateQuantized a) {
    s.Int(out a.clientId, 0, MaxClients - 1);
    s.Int(out a.headPositionX, MinPositionXZ, MaxPositionXZ);
    s.Int(out a.headPositionY, MinPositionY, MaxPositionY);
    s.Int(out a.headPositionZ, MinPositionXZ, MaxPositionXZ);
    s.Bits(out a.headRotationLargest, 2);
    s.Bits(out a.headRotationX, RotationBits);
    s.Bits(out a.headRotationY, RotationBits);
    s.Bits(out a.headRotationZ, RotationBits);
    s.Int(out a.leftHandPositionX, MinPositionXZ, MaxPositionXZ);
    s.Int(out a.leftHandPositionY, MinPositionY, MaxPositionY);
    s.Int(out a.leftHandPositionZ, MinPositionXZ, MaxPositionXZ);
    s.Bits(out a.leftHandRotationLargest, 2);
    s.Bits(out a.leftHandRotationX, RotationBits);
    s.Bits(out a.leftHandRotationY, RotationBits);
    s.Bits(out a.leftHandRotationZ, RotationBits);
    s.Int(out a.leftHandGripTrigger, MinTrigger, MaxTrigger);
    s.Int(out a.leftHandIdTrigger, MinTrigger, MaxTrigger);
    s.Bool(out a.isLeftHandPointing);
    s.Bool(out a.areLeftHandThumbsUp);
    s.Bool(out a.isLeftHandHoldingCube);

    if (a.isLeftHandHoldingCube) {
      s.Int(out a.leftHandCubeId, 0, MaxCubes - 1);
      s.Bits(out a.leftHandAuthoritySequence, 16);
      s.Bits(out a.leftHandOwnershipSequence, 16);
      s.Int(out a.leftHandCubeLocalPositionX, MinLocalPosition, MaxLocalPosition);
      s.Int(out a.leftHandCubeLocalPositionY, MinLocalPosition, MaxLocalPosition);
      s.Int(out a.leftHandCubeLocalPositionZ, MinLocalPosition, MaxLocalPosition);
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

    s.Int(out a.rightHandPositionX, MinPositionXZ, MaxPositionXZ);
    s.Int(out a.rightHandPositionY, MinPositionY, MaxPositionY);
    s.Int(out a.rightHandPositionZ, MinPositionXZ, MaxPositionXZ);
    s.Bits(out a.rightHandRotationLargest, 2);
    s.Bits(out a.rightHandRotationX, RotationBits);
    s.Bits(out a.rightHandRotationY, RotationBits);
    s.Bits(out a.rightHandRotationZ, RotationBits);
    s.Int(out a.rightHandGripTrigger, MinTrigger, MaxTrigger);
    s.Int(out a.rightHandIndexTrigger, MinTrigger, MaxTrigger);
    s.Bool(out a.isRightHandPointing);
    s.Bool(out a.areRightHandThumbsUp);
    s.Bool(out a.isRightHandHoldingCube);

    if (a.isRightHandHoldingCube) {
      s.Int(out a.rightHandCubeId, 0, MaxCubes - 1);
      s.Bits(out a.rightHandAuthoritySequence, 16);
      s.Bits(out a.rightHandOwnershipSequence, 16);
      s.Int(out a.rightHandCubeLocalPositionX, MinLocalPosition, MaxLocalPosition);
      s.Int(out a.rightHandCubeLocalPositionY, MinLocalPosition, MaxLocalPosition);
      s.Int(out a.rightHandCubeLocalPositionZ, MinLocalPosition, MaxLocalPosition);
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
    s.Int(out a.voiceAmplitude, MinVoice, MaxVoice);
  }
}