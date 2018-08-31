using UnityEngine;

public static class OkwyVector3Extensions {
  public static Vector3 WithX(this Vector3 _, float value) {
    return new Vector3(_.x + value, _.y, _.z);
  }

  public static Vector3 WithY(this Vector3 _, float value) {
    return new Vector3(_.x, _.y + value, _.z);
  }

  public static Vector3 WithZ(this Vector3 _, float value) {
    return new Vector3(_.x, _.y, _.z + value);
  }
}