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
using static UnityEngine.Quaternion;
using static UnityEngine.Vector3;
using static System.Math;
using static Constants;
using UnityEngine.Serialization;

public class NetworkCube : UnityEngine.MonoBehaviour {
  public GameObject smoothed;
  public GameObject touching;
  public Context context;                                           // the context that this cube exists in. eg. blue context, red context, for loopback testing.
  public int cubeId = CollisionWithFloor;                           // the cube id in range [0,NumCubes-1]
  public bool isConfirmed = false;                                    // true if this cube has been confirmed under client authority by the server.
  public bool isPendingCommit = false;                                // true if this cube has returned to default authority and needs to be committed back to the server.
  public int authorityId;                                        // 0 = default authority (white), 1 = blue (client 0), 2 = red (client 2), and so on.
  public ushort ownershipSequence;                                  // sequence number increased on each ownership change (players grabs/release this cube)
  public ushort authoritySequence;                                  // sequence number increased on each authority change (eg. indirect interaction, such as being hit by an object thrown by a player)
  public int holderId = Nobody;                              // client id of player currently holding this cube. -1 if not currently being held.
  public Hands localAvatar;                                        // while this cube is held by the local player, this points to the local avatar.
  public Hands.HandData localHand;                                 // while this cube is held by the local player, this points to the local avatar hand that is holding it.
  public RemoteAvatar remoteAvatar;                                 // while this cube is held by a remote player, this points to the remote avatar.
  public RemoteAvatar.Hand remoteHand;                          // while this cube is held by a remote player, this points to the remote avatar hand that is holding it.
  public ulong activeFrame = 0;                                 // the frame number this cube was last active (not at rest). used to return to default authority (white) some amount of time after coming to rest.
  public long interactionFrame = -100000;                 // the last frame number this cube was held by a player. used to increase priority for objects for a few seconds after they are thrown.
  public Vector3 positionLag = zero;                      // the current position error between the physical cube and its visual representation.
  public Quaternion rotationLag = identity;            // the current rotation error between the physical cube and its visual representation.

  public enum HoldType {
    None,                                               // not currently being held
    LeftHand,                                           // held by left touch controller
    RightHand,                                          // held by right touch controller
  };

  public void Init(Context c, int id) {
    context = c;
    cubeId = id;
    touching.GetComponent<Touching>().Initialize(c, id);
    smoothed.transform.parent = null;
  }

  public bool HasHolder() => holderId != Nobody;
  public bool SameHolder(RemoteAvatar avatar, RemoteAvatar.Hand hand) => remoteAvatar == avatar && remoteHand == hand;

  public void LocalGrip(Hands hands, Hands.HandData h) {
    Release();
    localAvatar = hands;
    localHand = h;
    holderId = context.GetClientId();
    authorityId = context.GetAuthorityId();
    ownershipSequence++;
    authoritySequence = 0;
    touching.GetComponent<BoxCollider>().isTrigger = false;
    gameObject.GetComponent<Rigidbody>().isKinematic = true;
    h.grip = gameObject;
    gameObject.layer = context.GetGripLayer();
    gameObject.transform.SetParent(h.transform, true);
    h.supports = context.FindSupports(h.grip);
    hands.AttachCube(ref h);
  }

  public void RemoteGrip(RemoteAvatar avatar, RemoteAvatar.Hand h, int clientId) {
    Assert.IsTrue(clientId != context.GetClientId());
    Release();
    h.grip = gameObject;
    var rigidBody = gameObject.GetComponent<Rigidbody>();
    rigidBody.isKinematic = true;
    rigidBody.detectCollisions = false;
    gameObject.transform.SetParent(h.transform, true);
    remoteAvatar = avatar;
    remoteHand = h;
    holderId = clientId;
    authorityId = clientId + 1;
    avatar.CubeAttached(ref h);
  }

  /*
   * Detach cube from any player who is holding it (local or remote).
   */
  public void Release() {
    if (!HasHolder()) return;

    if (localAvatar) {
      localAvatar.DetachCube(ref localHand);
      touching.GetComponent<BoxCollider>().isTrigger = true;
    }

    if (remoteAvatar)
      remoteAvatar.DetachCube(ref remoteHand);

    localAvatar = null;
    localHand = null;
    remoteHand = null;
    remoteAvatar = null;
    holderId = Nobody;
  }

  /*
   * Collision Callback.
   * This is used to call into the collision callback on the context that owns these cubes,
   * which is used to track authority transfer (poorly), and to increase network priority 
   * for cubes that were recently in high energy collisions with other cubes, or the floor.
   */
  void OnCollisionEnter(Collision c) {
    var network = c.gameObject.GetComponent<NetworkCube>();
    int id1 = cubeId;

    int id2 = network == null
      ? CollisionWithFloor
      : network.cubeId;

    context.Collide(id1, id2, c);
  }

  /*
   * Moves the physical cube immediately, while the visual cube smoothly eases towards the corrected position over time.
   */
  public void SmoothMove(Vector3 position, Quaternion rotation) {
    var rigidBody = gameObject.GetComponent<Rigidbody>();
    var oldPosition = rigidBody.position + positionLag; //oldSmoothedPosition
    var oldRotation = rigidBody.rotation * rotationLag; //oldSmoothedRotation

    rigidBody.position = position;
    rigidBody.rotation = rotation;
    gameObject.transform.position = position;
    gameObject.transform.rotation = rotation;
    positionLag = oldPosition - position;
    rotationLag = Inverse(rotation) * oldRotation;
  }

  /*
   * Local version of function to move with smoothing. Used for cubes held in remote avatar hands.
   */
  public void LocalSmoothMove(Vector3 localPosition, Quaternion localRotation) {
    Assert.IsTrue(gameObject.transform.parent != null);
    var oldPosition = gameObject.transform.position + positionLag;
    var oldRotation = gameObject.transform.rotation * rotationLag;
    var position = gameObject.transform.position;
    var rotation = gameObject.transform.rotation;
    gameObject.transform.localPosition = localPosition;
    gameObject.transform.localRotation = localRotation;
    positionLag = oldPosition - position;
    rotationLag = oldRotation * Inverse(rotation);
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

    positionLag = positionLag.sqrMagnitude > epsilon
      ? positionLag * positionSmooth
      : zero;

    if (Abs(rotationLag.x) > epsilon
      || Abs(rotationLag.y) > epsilon
      || Abs(rotationLag.y) > epsilon 
      || Abs(1.0f - rotationLag.w) > epsilon
    )
      rotationLag = Slerp(rotationLag, identity, 1.0f - rotationSmooth);
    else
      rotationLag = identity;

    smoothed.transform.position = gameObject.transform.position + positionLag;
    smoothed.transform.rotation = gameObject.transform.rotation * rotationLag;
#endif // #if DISABLE_SMOOTHING
  }
}