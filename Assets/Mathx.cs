using System;
using UnityEngine;

public static class Mathx {
  public static int Clamp(int value, int min, int max) {
    if (value < min) return min;
    if (value > max) return max;

    return value;
  }

  public static void SetSmallestThree(Quaternion q, out uint largest, out uint rotationX, out uint rotationY, out uint rotationZ, int rotationBits = 20) {
    const float min = -1.0f / 1.414214f;       // 1.0f / sqrt(2)
    const float max = +1.0f / 1.414214f;
    float scale = (1 << rotationBits) - 1;
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

  public static void SetQuaternion(out Quaternion q, uint largest, uint rotationX, uint rotationY, uint rotationZ, int rotationBits = 20) {
    const float min = -1.0f / 1.414214f;       // 1.0f / sqrt(2)
    const float max = +1.0f / 1.414214f;
    float scale = (1 << rotationBits) - 1;
    float inverseScale = 1.0f / scale;
    var first = rotationX * inverseScale * (max - min) + min;
    var second = rotationY * inverseScale * (max - min) + min;
    var third = rotationZ * inverseScale * (max - min) + min;
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
      q = new Quaternion(x, y, z, w);
      var length = (float)Math.Sqrt(norm);
      q.x /= length;
      q.y /= length;
      q.z /= length;
      q.w /= length;
    } else {
      q = new Quaternion(0, 0, 0, 1);
    }
  }
}