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
using static Constants;
using static Mathx;

public struct CubeState {
  public static CubeState defaults;

  public bool isActive;

  public ushort 
    authoritySequence,
    ownershipSequence;

  public int
    authorityId,
    positionX,
    positionY,
    positionZ,
    linearVelocityX,
    linearVelocityY,
    linearVelocityZ,
    angularVelocityX,
    angularVelocityY,
    angularVelocityZ;

  public uint 
    rotationLargest,
    rotationX,
    rotationY,
    rotationZ;
}

public struct CubeDelta {
#if DEBUG_DELTA_COMPRESSION
    public int absolute_position_x;
    public int absolute_position_y;
    public int absolute_position_z;
#endif // #if DEBUG_DELTA_COMPRESSION

  public int 
    positionX,
    positionY,
    positionZ,
    linearVelocityX,
    linearVelocityY,
    linearVelocityZ,
    angularVelocityX,
    angularVelocityY,
    angularVelocityZ;
}

public class Snapshot {
  public CubeState[] states = new CubeState[MaxCubes];

  public static void QuaternionToSmallestThree(Quaternion q, out uint largest, out uint rotationX, out uint rotationY, out uint rotationZ) { //QuaternionToSmallestThree
    const float min = -1.0f / 1.414214f;       // 1.0f / sqrt(2)
    const float max = +1.0f / 1.414214f;
    const float scale = (1 << RotationBits) - 1;
    var maxAbs = Math.Abs(q.x);
    var absY = Math.Abs(q.y);
    var absZ = Math.Abs(q.z);
    var absW = Math.Abs(q.w);
    largest = 0;

    if (absY > maxAbs) {
      largest = 1;
      maxAbs = absY;
    }

    if (absZ > maxAbs) {
      largest = 2;
      maxAbs = absZ;
    }

    if (absW > maxAbs) {
      largest = 3;
      maxAbs = absW;
    }

    var first = 0f;
    var second = 0f;
    var third = 0f;

    if (largest == 0) {
      if (q.x >= 0) {
        first = q.y;
        second = q.z;
        third = q.w;

      } else {
        first = -q.y;
        second = -q.z;
        third = -q.w;
      }

    } else if (largest == 1) {
      if (q.y >= 0) {
        first = q.x;
        second = q.z;
        third = q.w;

      } else {
        first = -q.x;
        second = -q.z;
        third = -q.w;
      }

    } else if (largest == 2) {
      if (q.z >= 0) {
        first = q.x;
        second = q.y;
        third = q.w;

      } else {
        first = -q.x;
        second = -q.y;
        third = -q.w;
      }

    } else if (largest == 3) {
      if (q.w >= 0) {
        first = q.x;
        second = q.y;
        third = q.z;

      } else {
        first = -q.x;
        second = -q.y;
        third = -q.z;
      }
    }

    rotationX = (uint)Math.Floor((first - min) / (max - min) * scale + 0.5f);
    rotationY = (uint)Math.Floor((second - min) / (max - min) * scale + 0.5f);
    rotationZ = (uint)Math.Floor((third - min) / (max - min) * scale + 0.5f);
  }

  public static Quaternion SmallestThreeToQuaternion(uint largest, uint rotationX, uint rotationY, uint rotationZ) {
    const float min = -1.0f / 1.414214f;       // 1.0f / sqrt(2)
    const float max = +1.0f / 1.414214f;
    const float scale = (1 << RotationBits) - 1;
    const float inverse_scale = 1.0f / scale;
    var first = rotationX * inverse_scale * (max - min) + min;
    var second = rotationY * inverse_scale * (max - min) + min;
    var third = rotationZ * inverse_scale * (max - min) + min;
    var x = 0.0f;
    var y = 0.0f;
    var z = 0.0f;
    var w = 0.0f;

    if (largest == 0) {
      x = (float)Math.Sqrt(1 - first * first - second * second - third * third);
      y = first;
      z = second;
      w = third;

    } else if (largest == 1) {
      x = first;
      y = (float)Math.Sqrt(1 - first * first - second * second - third * third);
      z = second;
      w = third;

    } else if (largest == 2) {
      x = first;
      y = second;
      z = (float)Math.Sqrt(1 - first * first - second * second - third * third);
      w = third;

    } else if (largest == 3) {
      x = first;
      y = second;
      z = third;
      w = (float)Math.Sqrt(1 - first * first - second * second - third * third);
    }
    
    var norm = x * x + y * y + z * z + w * w; //IMPORTANT: We must normalize the quaternion here because it will have slight drift otherwise due to being quantized

    if (norm > 0.000001f) {
      var quaternion = new Quaternion(x, y, z, w);
      var length = (float)Math.Sqrt(norm);
      quaternion.x /= length;
      quaternion.y /= length;
      quaternion.z /= length;
      quaternion.w /= length;

      return quaternion;
    } else {
      return new Quaternion(0, 0, 0, 1);
    }
  }

  public static void ClampPosition(ref int x, ref int y, ref int z) {
    x = Clamp(x, MinPositionXZ, MinPositionXZ);
    y = Clamp(y, MinPositionY, MaxPositionY);
    z = Clamp(z, MinPositionXZ, MinPositionXZ);
  }

  public static void ClampLinearVelocity(ref int x, ref int y, ref int z) {
    x = Clamp(x, LinearVelocityMinimum, LinearVelocityMaximum);
    y = Clamp(y, LinearVelocityMinimum, LinearVelocityMaximum);
    z = Clamp(z, LinearVelocityMinimum, LinearVelocityMaximum);
  }

  public static void ClampAngularVelocity(ref int x, ref int y, ref int z) {
    x = Clamp(x, AngularVelocityMinimum, AngularVelocityMaximum);
    y = Clamp(y, AngularVelocityMinimum, AngularVelocityMaximum);
    z = Clamp(z, AngularVelocityMinimum, AngularVelocityMaximum);
  }

  public static void ClampLocalPosition(ref int x, ref int y, ref int z) {
    x = Clamp(x, MinLocalPosition, MaxLocalPosition);
    y = Clamp(y, MinLocalPosition, MaxLocalPosition);
    z = Clamp(z, MinLocalPosition, MaxLocalPosition);
  }

  public static void GetState(Rigidbody rigidbody, NetworkCube network, ref CubeState s, ref Vector3 origin) {
    s.isActive = !rigidbody.IsSleeping();
    s.authorityId = network.authorityId;
    s.authoritySequence = network.authorityPacketId;
    s.ownershipSequence = network.ownershipId;

    var position = rigidbody.position - origin;
    s.positionX = (int)Math.Floor(position.x * UnitsPerMeter + 0.5f);
    s.positionY = (int)Math.Floor(position.y * UnitsPerMeter + 0.5f);
    s.positionZ = (int)Math.Floor(position.z * UnitsPerMeter + 0.5f);
    QuaternionToSmallestThree(rigidbody.rotation, out s.rotationLargest, out s.rotationX, out s.rotationY, out s.rotationZ);

    s.linearVelocityX = (int)Math.Floor(rigidbody.velocity.x * UnitsPerMeter + 0.5f);
    s.linearVelocityY = (int)Math.Floor(rigidbody.velocity.y * UnitsPerMeter + 0.5f);
    s.linearVelocityZ = (int)Math.Floor(rigidbody.velocity.z * UnitsPerMeter + 0.5f);
    s.angularVelocityX = (int)Math.Floor(rigidbody.angularVelocity.x * UnitsPerMeter + 0.5f);
    s.angularVelocityY = (int)Math.Floor(rigidbody.angularVelocity.y * UnitsPerMeter + 0.5f);
    s.angularVelocityZ = (int)Math.Floor(rigidbody.angularVelocity.z * UnitsPerMeter + 0.5f);

    ClampPosition(ref s.positionX, ref s.positionY, ref s.positionZ);
    ClampLinearVelocity(ref s.linearVelocityX, ref s.linearVelocityY, ref s.linearVelocityZ);
    ClampAngularVelocity(ref s.angularVelocityX, ref s.angularVelocityY, ref s.angularVelocityZ);
  }

  public static void ApplyState(Rigidbody rigidbody, NetworkCube network, ref CubeState s, ref Vector3 origin, bool isSmooth = false) {
    network.Release();

    if (s.isActive && rigidbody.IsSleeping())
        rigidbody.WakeUp();

    if (!s.isActive && !rigidbody.IsSleeping())
        rigidbody.Sleep();

    network.authorityId = s.authorityId;
    network.authorityPacketId = s.authoritySequence;
    network.ownershipId = s.ownershipSequence;

    var position = new Vector3(s.positionX, s.positionY, s.positionZ) * 1.0f / UnitsPerMeter + origin;
    var rotation = SmallestThreeToQuaternion(s.rotationLargest, s.rotationX, s.rotationY, s.rotationZ);

    if (isSmooth) {
      network.SmoothMove(position, rotation);
    } else {
      rigidbody.position = position;
      rigidbody.rotation = rotation;
    }

    rigidbody.velocity = new Vector3(s.linearVelocityX, s.linearVelocityY, s.linearVelocityZ) * 1.0f / UnitsPerMeter;
    rigidbody.angularVelocity = new Vector3(s.angularVelocityX, s.angularVelocityY, s.angularVelocityZ) * 1.0f / UnitsPerMeter;
  }
}