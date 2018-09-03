/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */

using Network;
using System;
using UnityEngine;
using UnityEngine.Assertions;
using Pose = OvrAvatarDriver.Pose;
using static UnityEngine.Quaternion;
using static UnityEngine.Vector3;
using static System.Math;
using static Snapshot;
using static Constants;
using static Mathx;

public struct AvatarStateQuantized {
  public static AvatarStateQuantized Default;

  public int 
    clientId,
    headPositionX,
    headPositionY,
    headPositionZ,
    leftHandPositionX,
    leftHandPositionY,
    leftHandPositionZ,
    leftHandGripTrigger,
    leftHandIdTrigger,
    leftHandCubeId,
    leftHandCubeLocalPositionX,
    leftHandCubeLocalPositionY,
    leftHandCubeLocalPositionZ,
    rightHandPositionX,
    rightHandPositionY,
    rightHandPositionZ,
    rightHandGripTrigger,
    rightHandIndexTrigger,
    rightHandCubeId,
    rightHandCubeLocalPositionX,
    rightHandCubeLocalPositionY,
    rightHandCubeLocalPositionZ,
    voiceAmplitude;

  public uint 
    headRotationLargest,
    headRotationX,
    headRotationY,
    headRotationZ,
    leftHandRotationLargest,
    leftHandRotationX,
    leftHandRotationY,
    leftHandRotationZ,
    leftHandCubeLocalRotationLargest,
    leftHandCubeLocalRotationX,
    leftHandCubeLocalRotationY,
    leftHandCubeLocalRotationZ,
    rightHandRotationLargest,
    rightHandRotationX,
    rightHandRotationY,
    rightHandRotationZ,
    rightHandCubeLocalRotationLargest,
    rightHandCubeLocalRotationX,
    rightHandCubeLocalRotationY,
    rightHandCubeLocalRotationZ;

  public ushort 
    leftHandAuthoritySequence,
    leftHandOwnershipSequence,
    rightHandAuthoritySequence,
    rightHandOwnershipSequence;

  public bool 
    isLeftHandPointing,
    areLeftHandThumbsUp,
    isLeftHandHoldingCube,
    isRightHandPointing,
    areRightHandThumbsUp,
    isRightHandHoldingCube;
}

public struct AvatarState {
  public static AvatarState Default;

  public Vector3 
    headPosition,
    leftHandPosition,
    leftHandCubeLocalPosition,
    rightHandPosition,
    rightHandCubeLocalPosition;

  public Quaternion 
    headRotation,
    leftHandRotation,
    leftHandCubeLocalRotation,
    rightHandRotation,
    rightHandCubeLocalRotation;

  public int 
    clientId,
    leftHandCubeId,
    rightHandCubeId;

  public float 
    leftHandGripTrigger,
    leftHandIdTrigger,
    rightHandGripTrigger,
    rightHandIdTrigger,
    voiceAmplitude;

  public ushort 
    leftHandAuthoritySequence,
    leftHandOwnershipSequence,
    rightHandAuthoritySequence,
    rightHandOwnershipSequence;

  public bool 
    isLeftHandPointing,
    areLeftHandThumbsUp,
    isLeftHandHoldingCube,
    isRightHandPointing,
    areRightHandThumbsUp,
    isRightHandHoldingCube;

  public static void Initialize(out AvatarState s, int clientId, Pose p, GameObject leftHandHeldObj, GameObject rightHandHeldObj) {
    s.clientId = clientId;
    s.headPosition = p.headPosition;
    s.headRotation = p.headRotation;
    s.leftHandPosition = p.handLeftPosition;
    s.leftHandRotation = p.handLeftRotation;
    s.leftHandGripTrigger = p.handLeftPose.gripFlex;
    s.leftHandIdTrigger = p.handLeftPose.indexFlex;
    s.isLeftHandPointing = p.handLeftPose.isPointing;
    s.areLeftHandThumbsUp = p.handLeftPose.isThumbUp;

    if (leftHandHeldObj) {
      s.isLeftHandHoldingCube = true;
      var network = leftHandHeldObj.GetComponent<NetworkCube>();
      s.leftHandCubeId = network.cubeId;
      s.leftHandAuthoritySequence = network.GetAuthoritySequence();
      s.leftHandOwnershipSequence = network.GetOwnershipSequence();
      s.leftHandCubeLocalPosition = leftHandHeldObj.transform.localPosition;
      s.leftHandCubeLocalRotation = leftHandHeldObj.transform.localRotation;
    } else {
      s.isLeftHandHoldingCube = false;
      s.leftHandCubeId = -1;
      s.leftHandAuthoritySequence = 0;
      s.leftHandOwnershipSequence = 0;
      s.leftHandCubeLocalPosition = zero;
      s.leftHandCubeLocalRotation = identity;
    }

    s.rightHandPosition = p.handRightPosition;
    s.rightHandRotation = p.handRightRotation;
    s.rightHandGripTrigger = p.handRightPose.gripFlex;
    s.rightHandIdTrigger = p.handRightPose.indexFlex;
    s.isRightHandPointing = p.handRightPose.isPointing;
    s.areRightHandThumbsUp = p.handRightPose.isThumbUp;

    if (rightHandHeldObj) {
      s.isRightHandHoldingCube = true;
      var network = rightHandHeldObj.GetComponent<NetworkCube>();
      s.rightHandCubeId = network.cubeId;
      s.rightHandAuthoritySequence = network.GetAuthoritySequence();
      s.rightHandOwnershipSequence = network.GetOwnershipSequence();
      s.rightHandCubeLocalPosition = rightHandHeldObj.transform.localPosition;
      s.rightHandCubeLocalRotation = rightHandHeldObj.transform.localRotation;
    } else {
      s.isRightHandHoldingCube = false;
      s.rightHandCubeId = -1;
      s.rightHandAuthoritySequence = 0;
      s.rightHandOwnershipSequence = 0;
      s.rightHandCubeLocalPosition = zero;
      s.rightHandCubeLocalRotation = identity;
    }
    s.voiceAmplitude = p.voiceAmplitude;
  }

  public static void ApplyPose(ref AvatarState s, int clientId, Pose p, Context context) {
    p.headPosition = s.headPosition;
    p.headRotation = s.headRotation;
    p.handLeftPosition = s.leftHandPosition;
    p.handLeftRotation = s.leftHandRotation;
    p.handLeftPose.gripFlex = s.leftHandGripTrigger;
    p.handLeftPose.indexFlex = s.leftHandIdTrigger;
    p.handLeftPose.isPointing = s.isLeftHandPointing;
    p.handLeftPose.isThumbUp = s.areLeftHandThumbsUp;
    p.handRightPosition = s.rightHandPosition;
    p.handRightRotation = s.rightHandRotation;
    p.handRightPose.gripFlex = s.rightHandGripTrigger;
    p.handRightPose.indexFlex = s.rightHandIdTrigger;
    p.handRightPose.isPointing = s.isRightHandPointing;
    p.handRightPose.isThumbUp = s.areRightHandThumbsUp;
    p.voiceAmplitude = s.voiceAmplitude;
  }

  public static void UpdateLeftHandSequenceNumbers(ref AvatarState s, Context context) {
    if (!s.isLeftHandHoldingCube) return;

    var n = context.GetCube(s.leftHandCubeId).GetComponent<NetworkCube>();
    if (!Util.SequenceGreaterThan(s.leftHandOwnershipSequence, n.GetOwnershipSequence())) return;
#if DEBUG_AUTHORITY
    Debug.Log( "server -> client: update left hand sequence numbers - ownership sequence " + network.GetOwnershipSequence() + "->" + s.leftHandOwnershipSequence + ", authority sequence " + network.GetOwnershipSequence() + "->" + s.leftHandAuthoritySequence );
#endif // #if DEBUG_AUTHORITY
    n.SetOwnershipSequence(s.leftHandOwnershipSequence);
    n.SetAuthoritySequence(s.leftHandAuthoritySequence);
  }

  public static void UpdateRightHandSequenceNumbers(ref AvatarState s, Context context) {
    if (!s.isRightHandHoldingCube) return;

    var n = context.GetCube(s.rightHandCubeId).GetComponent<NetworkCube>();
    if (!Util.SequenceGreaterThan(s.rightHandOwnershipSequence, n.GetOwnershipSequence())) return;
#if DEBUG_AUTHORITY
    Debug.Log( "server -> client: update right hand sequence numbers - ownership sequence " + network.GetOwnershipSequence() + "->" + s.rightHandOwnershipSequence + ", authority sequence " + network.GetOwnershipSequence() + "->" + s.rightHandAuthoritySequence );
#endif // #if DEBUG_AUTHORITY
    n.SetOwnershipSequence(s.rightHandOwnershipSequence);
    n.SetAuthoritySequence(s.rightHandAuthoritySequence);
  }

  public static void ApplyLeftHandUpdate(ref AvatarState s, int clientId, Context context, RemoteAvatar avatar) {
    Assert.IsTrue(clientId == s.clientId);
    if (!s.isLeftHandHoldingCube) return;

    var n = context.GetCube(s.leftHandCubeId).GetComponent<NetworkCube>();

    if (!n.HasRemoteHolder(avatar, avatar.GetLeftHand()))
      n.AttachToRemotePlayer(avatar, avatar.GetLeftHand(), s.clientId);

    n.SetAuthoritySequence(s.leftHandAuthoritySequence);
    n.SetOwnershipSequence(s.leftHandOwnershipSequence);
    n.LocalSmoothMove(s.leftHandCubeLocalPosition, s.leftHandCubeLocalRotation);
  }

  public static void ApplyRightHandUpdate(ref AvatarState s, int clientId, Context context, RemoteAvatar avatar) {
    Assert.IsTrue(clientId == s.clientId);
    if (!s.isRightHandHoldingCube) return;

    var n = context.GetCube(s.rightHandCubeId).GetComponent<NetworkCube>();

    if (!n.HasRemoteHolder(avatar, avatar.GetRightHand()))
      n.AttachToRemotePlayer(avatar, avatar.GetRightHand(), s.clientId);

    n.SetAuthoritySequence(s.rightHandAuthoritySequence);
    n.SetOwnershipSequence(s.rightHandOwnershipSequence);
    n.LocalSmoothMove(s.rightHandCubeLocalPosition, s.rightHandCubeLocalRotation);
  }

  public static void Quantize(ref AvatarState s, out AvatarStateQuantized q) {
    q.clientId = s.clientId;
    q.headPositionX = (int)Floor(s.headPosition.x * UnitsPerMeter + 0.5f);
    q.headPositionY = (int)Floor(s.headPosition.y * UnitsPerMeter + 0.5f);
    q.headPositionZ = (int)Floor(s.headPosition.z * UnitsPerMeter + 0.5f);
    SetSmallestThree(s.headRotation, out q.headRotationLargest, out q.headRotationX, out q.headRotationY, out q.headRotationZ);

    q.leftHandPositionX = (int)Floor(s.leftHandPosition.x * UnitsPerMeter + 0.5f);
    q.leftHandPositionY = (int)Floor(s.leftHandPosition.y * UnitsPerMeter + 0.5f);
    q.leftHandPositionZ = (int)Floor(s.leftHandPosition.z * UnitsPerMeter + 0.5f);
    SetSmallestThree(s.leftHandRotation, out q.leftHandRotationLargest, out q.leftHandRotationX, out q.leftHandRotationY, out q.leftHandRotationZ);

    q.leftHandGripTrigger = (int)Floor(s.leftHandGripTrigger * TriggerMaximum + 0.5f);
    q.leftHandIdTrigger = (int)Floor(s.leftHandIdTrigger * TriggerMaximum + 0.5f);
    q.isLeftHandPointing = s.isLeftHandPointing;
    q.areLeftHandThumbsUp = s.areLeftHandThumbsUp;

    if (s.isLeftHandHoldingCube) {
      q.isLeftHandHoldingCube = true;
      q.leftHandCubeId = s.leftHandCubeId;
      q.leftHandAuthoritySequence = s.leftHandAuthoritySequence;
      q.leftHandOwnershipSequence = s.leftHandOwnershipSequence;
      q.leftHandCubeLocalPositionX = (int)Floor(s.leftHandCubeLocalPosition.x * UnitsPerMeter + 0.5f);
      q.leftHandCubeLocalPositionY = (int)Floor(s.leftHandCubeLocalPosition.y * UnitsPerMeter + 0.5f);
      q.leftHandCubeLocalPositionZ = (int)Floor(s.leftHandCubeLocalPosition.z * UnitsPerMeter + 0.5f);
      SetSmallestThree(s.leftHandCubeLocalRotation, out q.leftHandCubeLocalRotationLargest, out q.leftHandCubeLocalRotationX, out q.leftHandCubeLocalRotationY, out q.leftHandCubeLocalRotationZ);
    } else {
      q.isLeftHandHoldingCube = false;
      q.leftHandCubeId = -1;
      q.leftHandAuthoritySequence = 0;
      q.leftHandOwnershipSequence = 0;
      q.leftHandCubeLocalPositionX = 0;
      q.leftHandCubeLocalPositionY = 0;
      q.leftHandCubeLocalPositionZ = 0;
      q.leftHandCubeLocalRotationLargest = 0;
      q.leftHandCubeLocalRotationX = 0;
      q.leftHandCubeLocalRotationY = 0;
      q.leftHandCubeLocalRotationZ = 0;
    }

    q.rightHandPositionX = (int)Floor(s.rightHandPosition.x * UnitsPerMeter + 0.5f);
    q.rightHandPositionY = (int)Floor(s.rightHandPosition.y * UnitsPerMeter + 0.5f);
    q.rightHandPositionZ = (int)Floor(s.rightHandPosition.z * UnitsPerMeter + 0.5f);
    SetSmallestThree(s.rightHandRotation, out q.rightHandRotationLargest, out q.rightHandRotationX, out q.rightHandRotationY, out q.rightHandRotationZ);

    q.rightHandGripTrigger = (int)Floor(s.rightHandGripTrigger * TriggerMaximum + 0.5f);
    q.rightHandIndexTrigger = (int)Floor(s.rightHandIdTrigger * TriggerMaximum + 0.5f);
    q.isRightHandPointing = s.isRightHandPointing;
    q.areRightHandThumbsUp = s.areRightHandThumbsUp;

    if (s.isRightHandHoldingCube) {
      q.isRightHandHoldingCube = true;
      q.rightHandCubeId = s.rightHandCubeId;
      q.rightHandAuthoritySequence = s.rightHandAuthoritySequence;
      q.rightHandOwnershipSequence = s.rightHandOwnershipSequence;
      q.rightHandCubeLocalPositionX = (int)Floor(s.rightHandCubeLocalPosition.x * UnitsPerMeter + 0.5f);
      q.rightHandCubeLocalPositionY = (int)Floor(s.rightHandCubeLocalPosition.y * UnitsPerMeter + 0.5f);
      q.rightHandCubeLocalPositionZ = (int)Floor(s.rightHandCubeLocalPosition.z * UnitsPerMeter + 0.5f);
      SetSmallestThree(s.rightHandCubeLocalRotation, out q.rightHandCubeLocalRotationLargest, out q.rightHandCubeLocalRotationX, out q.rightHandCubeLocalRotationY, out q.rightHandCubeLocalRotationZ);
    } else {
      q.isRightHandHoldingCube = false;
      q.rightHandCubeId = -1;
      q.rightHandAuthoritySequence = 0;
      q.rightHandOwnershipSequence = 0;
      q.rightHandCubeLocalPositionX = 0;
      q.rightHandCubeLocalPositionY = 0;
      q.rightHandCubeLocalPositionZ = 0;
      q.rightHandCubeLocalRotationLargest = 0;
      q.rightHandCubeLocalRotationX = 0;
      q.rightHandCubeLocalRotationY = 0;
      q.rightHandCubeLocalRotationZ = 0;
    }

    q.voiceAmplitude = (int)Floor(s.voiceAmplitude * VoiceMaximum + 0.5f);    
    ClampPosition(ref q.headPositionX, ref q.headPositionY, ref q.headPositionZ); //clamp everything
    ClampPosition(ref q.leftHandPositionX, ref q.leftHandPositionY, ref q.leftHandPositionZ);
    ClampPosition(ref q.rightHandPositionX, ref q.rightHandPositionY, ref q.rightHandPositionZ);

    if (q.isLeftHandHoldingCube)
      ClampLocalPosition(ref q.leftHandCubeLocalPositionX, ref q.leftHandCubeLocalPositionY, ref q.leftHandCubeLocalPositionZ);

    if (q.isRightHandHoldingCube)
      ClampLocalPosition(ref q.rightHandCubeLocalPositionX, ref q.rightHandCubeLocalPositionY, ref q.rightHandCubeLocalPositionZ);
  }

  public static void Unquantize(ref AvatarStateQuantized q, out AvatarState s) {
    s.clientId = q.clientId;
    s.headPosition = new Vector3(q.headPositionX, q.headPositionY, q.headPositionZ) * 1.0f / UnitsPerMeter;
    SetQuaternion(out s.headRotation, q.headRotationLargest, q.headRotationX, q.headRotationY, q.headRotationZ);
    s.leftHandPosition = new Vector3(q.leftHandPositionX, q.leftHandPositionY, q.leftHandPositionZ) * 1.0f / UnitsPerMeter;
    s.leftHandRotation = SmallestThreeToQuaternion(q.leftHandRotationLargest, q.leftHandRotationX, q.leftHandRotationY, q.leftHandRotationZ);
    s.leftHandGripTrigger = q.leftHandGripTrigger * 1.0f / TriggerMaximum;
    s.leftHandIdTrigger = q.leftHandIdTrigger * 1.0f / TriggerMaximum;
    s.isLeftHandPointing = q.isLeftHandPointing;
    s.areLeftHandThumbsUp = q.areLeftHandThumbsUp;
    s.isLeftHandHoldingCube = q.isLeftHandHoldingCube;
    s.leftHandCubeId = q.leftHandCubeId;
    s.leftHandOwnershipSequence = q.leftHandOwnershipSequence;
    s.leftHandAuthoritySequence = q.leftHandAuthoritySequence;
    s.leftHandCubeLocalPosition = new Vector3(q.leftHandCubeLocalPositionX, q.leftHandCubeLocalPositionY, q.leftHandCubeLocalPositionZ) * 1.0f / UnitsPerMeter;
    s.leftHandCubeLocalRotation = SmallestThreeToQuaternion(q.leftHandCubeLocalRotationLargest, q.leftHandCubeLocalRotationX, q.leftHandCubeLocalRotationY, q.leftHandCubeLocalRotationZ);
    s.rightHandPosition = new Vector3(q.rightHandPositionX, q.rightHandPositionY, q.rightHandPositionZ) * 1.0f / UnitsPerMeter;
    s.rightHandRotation = SmallestThreeToQuaternion(q.rightHandRotationLargest, q.rightHandRotationX, q.rightHandRotationY, q.rightHandRotationZ);
    s.rightHandGripTrigger = q.rightHandGripTrigger * 1.0f / TriggerMaximum;
    s.rightHandIdTrigger = q.rightHandIndexTrigger * 1.0f / TriggerMaximum;
    s.isRightHandPointing = q.isRightHandPointing;
    s.areRightHandThumbsUp = q.areRightHandThumbsUp;
    s.isRightHandHoldingCube = q.isRightHandHoldingCube;
    s.rightHandCubeId = q.rightHandCubeId;
    s.rightHandOwnershipSequence = q.rightHandOwnershipSequence;
    s.rightHandAuthoritySequence = q.rightHandAuthoritySequence;
    s.rightHandCubeLocalPosition = new Vector3(q.rightHandCubeLocalPositionX, q.rightHandCubeLocalPositionY, q.rightHandCubeLocalPositionZ) * 1.0f / UnitsPerMeter;
    s.rightHandCubeLocalRotation = SmallestThreeToQuaternion(q.rightHandCubeLocalRotationLargest, q.rightHandCubeLocalRotationX, q.rightHandCubeLocalRotationY, q.rightHandCubeLocalRotationZ);
    s.voiceAmplitude = q.voiceAmplitude * 1.0f / VoiceMaximum;
  }

  public static void Interpolate(ref AvatarState from, ref AvatarState to, out AvatarState s, float time) {
    s.clientId = from.clientId; //convention: logically everything stays at the oldest sample, but positions and rotations and other continuous quantities are interpolated forward where it makes sense.
    s.headPosition = from.headPosition * (1-time) + to.headPosition * time;
    s.headRotation = Slerp(from.headRotation, to.headRotation, time);
    s.leftHandPosition = from.leftHandPosition * (1-time) + to.leftHandPosition * time;
    s.leftHandRotation = Slerp(from.leftHandRotation, to.leftHandRotation, time);
    s.leftHandGripTrigger = from.leftHandGripTrigger * (1-time) + to.leftHandGripTrigger * time;
    s.leftHandIdTrigger = from.leftHandIdTrigger * (1-time) + to.leftHandIdTrigger * time;
    s.isLeftHandPointing = from.isLeftHandPointing;
    s.areLeftHandThumbsUp = from.areLeftHandThumbsUp;
    s.isLeftHandHoldingCube = from.isLeftHandHoldingCube;
    s.leftHandCubeId = from.leftHandCubeId;
    s.leftHandAuthoritySequence = from.leftHandAuthoritySequence;
    s.leftHandOwnershipSequence = from.leftHandOwnershipSequence;

    if (from.isLeftHandHoldingCube == to.isLeftHandHoldingCube && from.leftHandCubeId == to.leftHandCubeId) {
      s.leftHandCubeLocalPosition = from.leftHandCubeLocalPosition * (1-time) + to.leftHandCubeLocalPosition * time;
      s.leftHandCubeLocalRotation = Slerp(from.leftHandCubeLocalRotation, to.leftHandCubeLocalRotation, time);
    } else {
      s.leftHandCubeLocalPosition = from.leftHandCubeLocalPosition;
      s.leftHandCubeLocalRotation = from.leftHandCubeLocalRotation;
    }

    s.rightHandPosition = from.rightHandPosition * (1-time) + to.rightHandPosition * time;
    s.rightHandRotation = Slerp(from.rightHandRotation, to.rightHandRotation, time);
    s.rightHandGripTrigger = from.rightHandGripTrigger * (1-time) + to.rightHandGripTrigger * time;
    s.rightHandIdTrigger = from.rightHandIdTrigger * (1-time) + to.rightHandIdTrigger * time;
    s.isRightHandPointing = from.isRightHandPointing;
    s.areRightHandThumbsUp = from.areRightHandThumbsUp;
    s.isRightHandHoldingCube = from.isRightHandHoldingCube;
    s.rightHandCubeId = from.rightHandCubeId;
    s.rightHandAuthoritySequence = from.rightHandAuthoritySequence;
    s.rightHandOwnershipSequence = from.rightHandOwnershipSequence;

    if (from.isRightHandHoldingCube == to.isRightHandHoldingCube && from.rightHandCubeId == to.rightHandCubeId) {
      s.rightHandCubeLocalPosition = from.rightHandCubeLocalPosition * (1-time) + to.rightHandCubeLocalPosition * time;
      s.rightHandCubeLocalRotation = Slerp(from.rightHandCubeLocalRotation, to.rightHandCubeLocalRotation, time);
    } else {
      s.rightHandCubeLocalPosition = from.rightHandCubeLocalPosition;
      s.rightHandCubeLocalRotation = from.rightHandCubeLocalRotation;
    }
    s.voiceAmplitude = from.voiceAmplitude * (time - 1) + to.voiceAmplitude * time;
  }
}