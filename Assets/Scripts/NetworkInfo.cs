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
using UnityEngine.Assertions;

public class NetworkInfo : MonoBehaviour {
  const int collisionWithFloor = -1;
  const int nobody = -1;
  public GameObject smoothed;
  public GameObject touching;
  public Context m_context;                                           // the context that this cube exists in. eg. blue context, red context, for loopback testing.
  public int m_cubeId = collisionWithFloor;                           // the cube id in range [0,NumCubes-1]
  public bool m_confirmed = false;                                    // true if this cube has been confirmed under client authority by the server.
  public bool m_pendingCommit = false;                                // true if this cube has returned to default authority and needs to be committed back to the server.
  public int m_authorityIndex;                                        // 0 = default authority (white), 1 = blue (client 0), 2 = red (client 2), and so on.
  public ushort m_ownershipSequence;                                  // sequence number increased on each ownership change (players grabs/release this cube)
  public ushort m_authoritySequence;                                  // sequence number increased on each authority change (eg. indirect interaction, such as being hit by an object thrown by a player)
  public int m_holdClientIndex = nobody;                              // client id of player currently holding this cube. -1 if not currently being held.
  public HoldType m_holdType = HoldType.None;                         // while this cube is being held, this identifies whether it is being held in the left or right hand, or by the headset + controller fallback.
  public Avatar m_localAvatar;                                        // while this cube is held by the local player, this points to the local avatar.
  public Avatar.HandData m_localHand;                                 // while this cube is held by the local player, this points to the local avatar hand that is holding it.
  public RemoteAvatar m_remoteAvatar;                                 // while this cube is held by a remote player, this points to the remote avatar.
  public RemoteAvatar.HandData m_remoteHand;                          // while this cube is held by a remote player, this points to the remote avatar hand that is holding it.
  public ulong m_lastActiveFrame = 0;                                 // the frame number this cube was last active (not at rest). used to return to default authority (white) some amount of time after coming to rest.
  public long m_lastPlayerInteractionFrame = -100000;                 // the last frame number this cube was held by a player. used to increase priority for objects for a few seconds after they are thrown.
  public Vector3 m_positionError = Vector3.zero;                      // the current position error between the physical cube and its visual representation.
  public Quaternion m_rotationError = Quaternion.identity;            // the current rotation error between the physical cube and its visual representation.

  public enum HoldType {
    None,                                               // not currently being held
    LeftHand,                                           // held by left touch controller
    RightHand,                                          // held by right touch controller
  };

  public void Init(Context context, int cubeId) {
    m_context = context;
    m_cubeId = cubeId;
    touching.GetComponent<Touching>().Initialize(context, cubeId);
    smoothed.transform.parent = null;
  }

  public void SetAuthorityId(int id) => m_authorityIndex = id;
  public void IncreaseAuthoritySequence() => m_authoritySequence++;
  public void IncreaseOwnershipSequence() => m_ownershipSequence++;
  public void SetAuthoritySequence(ushort sequence) => m_authoritySequence = sequence;
  public void SetOwnershipSequence(ushort sequence) => m_ownershipSequence = sequence;
  public void SetLastActiveFrame(ulong frame) => m_lastActiveFrame = frame;
  public void ClearConfirmed() => m_confirmed = false;
  public void SetConfirmed() => m_confirmed = true;
  public bool IsConfirmed() => m_confirmed;
  public void SetPendingCommit() => m_pendingCommit = true;
  public void ClearPendingCommit() => m_pendingCommit = false;
  public bool IsPendingCommit() => m_pendingCommit;
  public int GetCubeId() => m_cubeId;
  public int GetAuthorityId() => m_authorityIndex;
  public int GetHoldClientId() => m_holdClientIndex;
  public long GetLastFrame() => m_lastPlayerInteractionFrame;
  public void SetLastFrame(long frame) => m_lastPlayerInteractionFrame = frame;
  public bool IsHeldByPlayer() => m_holdClientIndex != -1;
  public bool IsHeldByLocalPlayer() => m_localAvatar != null;
  public bool IsHeldByRemotePlayer(RemoteAvatar avatar, RemoteAvatar.HandData hand) => m_remoteAvatar == avatar && m_remoteHand == hand;
  public ushort GetOwnershipSequence() => m_ownershipSequence;
  public ushort GetAuthoritySequence() => m_authoritySequence;
  public ulong GetLastActiveFrame() => m_lastActiveFrame;
  public HoldType GetHoldType() => m_holdType;

  /*
   * Return true if the local player can grab this cube
   * This is true if:
   *  1. No other player is currently grabbing that cube (common case)
   *  2. The local player already grabbing the cube, and the time the cube was grabbed is older than the current input to grab this cube. This allows passing cubes from hand to hand.
   */
  public bool CanGrabCube(ulong gripInputStartFrame) {
    if (m_holdClientIndex == nobody) return true;
    if (m_localHand?.startFrame < gripInputStartFrame) return true; //gripObjectStartFrame

    return false;
  }

  /*
   * Attach cube to local player.
   */
  public void AttachCubeToLocalPlayer(Avatar avatar, Avatar.HandData h) {
    DetachCube();
    m_localAvatar = avatar;
    m_localHand = h;

    m_holdType = (h.id == Avatar.LeftHand)
      ? HoldType.LeftHand 
      : HoldType.RightHand;

    m_holdClientIndex = m_context.GetClientId();
    m_authorityIndex = m_context.GetAuthorityId();

    IncreaseOwnershipSequence();
    SetAuthoritySequence(0);
    touching.GetComponent<BoxCollider>().isTrigger = false;
    gameObject.GetComponent<Rigidbody>().isKinematic = true;
    h.grip = gameObject;
    gameObject.layer = m_context.GetGripLayer();
    gameObject.transform.SetParent(h.transform, true);
    h.supports = m_context.GetSupports(h.grip);
    avatar.CubeAttached(ref h);
  }

  /*
   * Attach cube to remote player
   */
  public void AttachCubeToRemotePlayer(RemoteAvatar avatar, RemoteAvatar.HandData h, int clientIndex) {
    Assert.IsTrue(clientIndex != m_context.GetClientId());
    DetachCube();
    h.gripObject = gameObject;
    var rigidBody = gameObject.GetComponent<Rigidbody>();
    rigidBody.isKinematic = true;
    rigidBody.detectCollisions = false;
    gameObject.transform.SetParent(h.transform, true);
    m_remoteAvatar = avatar;
    m_remoteHand = h;
    m_holdClientIndex = clientIndex;
    m_authorityIndex = clientIndex + 1;
    avatar.CubeAttached(ref h);
  }

  /*
   * Detach cube from any player who is holding it (local or remote).
   */
  public void DetachCube() {
    if (m_holdClientIndex == nobody) return;

    if (m_localAvatar) {
      m_localAvatar.CubeDetached(ref m_localHand);
      touching.GetComponent<BoxCollider>().isTrigger = true;
    }

    if (m_remoteAvatar)
      m_remoteAvatar.CubeDetached(ref m_remoteHand);

    m_localAvatar = null;
    m_localHand = null;
    m_remoteHand = null;
    m_remoteAvatar = null;
    m_holdType = HoldType.None;
    m_holdClientIndex = nobody;
  }

  /*
   * Collision Callback.
   * This is used to call into the collision callback on the context that owns these cubes,
   * which is used to track authority transfer (poorly), and to increase network priority 
   * for cubes that were recently in high energy collisions with other cubes, or the floor.
   */
  void OnCollisionEnter(Collision c) {
    var obj = c.gameObject;
    var network = obj.GetComponent<NetworkInfo>();

    int cubeId1 = m_cubeId;
    int cubeId2 = collisionWithFloor;                   // IMPORTANT: cube id of -1 represents a collision with the floor

    if (network != null)
      cubeId2 = network.GetCubeId();

    m_context.Collide(cubeId1, cubeId2, c);
  }

  /*
   * Moves the physical cube immediately, while the visual cube smoothly eases towards the corrected position over time.
   */
  public void SmoothMove(Vector3 position, Quaternion rotation) {
    var rigidBody = gameObject.GetComponent<Rigidbody>();
    var oldPosition = rigidBody.position + m_positionError; //oldSmoothedPosition
    var oldRotation = rigidBody.rotation * m_rotationError; //oldSmoothedRotation

    rigidBody.position = position;
    rigidBody.rotation = rotation;
    gameObject.transform.position = position;
    gameObject.transform.rotation = rotation;
    m_positionError = oldPosition - position;
    m_rotationError = Quaternion.Inverse(rotation) * oldRotation;
  }

  /*
   * Local version of function to move with smoothing. Used for cubes held in remote avatar hands.
   */
  public void MoveWithSmoothingLocal(Vector3 localPosition, Quaternion localRotation) {
    Assert.IsTrue(gameObject.transform.parent != null);
    var oldPosition = gameObject.transform.position + m_positionError;
    var oldRotation = gameObject.transform.rotation * m_rotationError;
    var position = gameObject.transform.position;
    var rotation = gameObject.transform.rotation;
    gameObject.transform.localPosition = localPosition;
    gameObject.transform.localRotation = localRotation;
    m_positionError = oldPosition - position;
    m_rotationError = oldRotation * Quaternion.Inverse(rotation);
  }

  /*
   * Ease the smoothed cube towards the physical cube by reducing the local error factors towards zero/identity.
   */
  public void Smooth() {
#if DISABLE_SMOOTHING
    smoothed.transform.position = gameObject.transform.position;
    smoothed.transform.rotation = gameObject.transform.rotation;
#else // #if DISABLE_SMOOTHING
    const float epsilon = 0.000001f;
    var positionSmooth = 0.95f;
    var rotationSmooth = 0.95f;

    if (gameObject.transform.parent != null) {      
      positionSmooth = 0.7f; //tight smoothing while held for player "snap to hand"
      rotationSmooth = 0.85f;
    }

    m_positionError = m_positionError.sqrMagnitude > epsilon
      ? m_positionError * positionSmooth
      : Vector3.zero;

    if (Math.Abs(m_rotationError.x) > epsilon
      || Math.Abs(m_rotationError.y) > epsilon
      || Math.Abs(m_rotationError.y) > epsilon 
      || Math.Abs(1.0f - m_rotationError.w) > epsilon
    )
      m_rotationError = Quaternion.Slerp(m_rotationError, Quaternion.identity, 1.0f - rotationSmooth);
    else
      m_rotationError = Quaternion.identity;

    smoothed.transform.position = gameObject.transform.position + m_positionError;
    smoothed.transform.rotation = gameObject.transform.rotation * m_rotationError;

#endif // #if DISABLE_SMOOTHING
  }
}