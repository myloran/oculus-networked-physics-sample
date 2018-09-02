/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using UnityEngine;
using System.Collections.Generic;
using static UnityEngine.Assertions.Assert;
using static UnityEngine.Quaternion;
using static UnityEngine.Vector3;
using static UnityEngine.Time;
using static System.Math;
using static Hands.HandState;
using static Constants;

/// <summary>
/// Handles pointing and grabbing for both hand
/// </summary>
public class Hands : OvrAvatarLocalDriver {
  public struct HandInput {
    public float handTrigger;
    public float indexTrigger;
    public float previousIndexTrigger;
    public ulong indexPressFrame;
    public bool isPointing;
    public bool isPressingX;
    public bool isPressingY;
    public Vector2 stick;
  }

  public enum HandState {
    Neutral,
    Pointing,
    Grip,
  }

  public class HandData {
    public struct ThrowRingBufferEntry {
      public bool valid; //is this really needed?
      public float speed;
    };

    public List<GameObject> supports;
    public Animator animator;
    public Transform transform;

    public GameObject line,
      point,
      grip;

    public ThrowRingBufferEntry[] throws = new ThrowRingBufferEntry[ThrowRingBufferSize];
    public HandInput input;
    public HandState state = Neutral;

    public Vector3 
      prevHandPosition,
      prevGripPosition;

    public Quaternion 
      prevHandRotation,
      prevGripRotation;

    public string name;

    public int 
      id,
      throwId;

    public bool 
      isTouching,
      hasReleaseVelocity;

    public ulong 
      pointFrame,
      inputFrame,
      startFrame,
      releaseFrame;
  };

  public const int 
    LeftHand = 0,
    RightHand = 1,
    PostReleaseDisableSelectFrames = 20,
    ThrowRingBufferSize = 16;

  const ulong 
    PointDebounceFrames = 10,
    PointStickyFrames = 45;

  const float
    LineWidth = 0.02f,
    RaycastDistance = 256.0f,
    PointSphereCastStart = 10.0f,
    PointSphereCastRadius = 0.25f,
    MinimumCubeHeight = 0.1f,
    IndexThreshold = 0.5f,
    IndexStickyFrames = 45,
    GripThreshold = 0.75f,
    GrabDistance = 1.0f,
    GrabRadius = 0.05f,
    WarpDistance = GrabDistance * 0.5f,
    ZoomMinimum = 0.4f,
    ZoomMaximum = 20.0f,
    ZoomSpeed = 4.0f,
    RotateSpeed = 180.0f,
    StickThreshold = 0.65f,
    ThrowSpeed = 5.0f,
    HardThrowSpeed = 100.0f,
    MaxThrowSpeed = 20.0f,
    ThrowVelocityMinY = 2.5f;

  public GameObject linePrefab;
  OvrAvatar avatar;
  Context context;

  HandData 
    left = new HandData(),
    right = new HandData();

  int cubesLayer;
  int gripLayer;
  int layerMask;

  public void SetContext(Context c) {
    IsNotNull(c);
    context = c;
    ResetHand(ref left);
    ResetHand(ref right);
    cubesLayer = c.gameObject.layer;
    gripLayer = c.gameObject.layer + 1;
    layerMask = (1 << cubesLayer) | (1 << gripLayer);
  }

  void Start() {
    IsNotNull(linePrefab);
    avatar = GetComponent<OvrAvatar>();
    left.id = LeftHand;
    right.id = RightHand;
    left.name = "left hand";
    right.name = "right hand";
    left.animator = avatar.HandLeft.animator;
    right.animator = avatar.HandRight.animator;
    left.transform = avatar.HandLeftRoot;
    right.transform = avatar.HandRightRoot;
    IsNotNull(left.transform);
    IsNotNull(right.transform);
  }

  void Update() {
    IsNotNull(context);
    Pose p;
    if (!GetPose(out p)) return;

    UpdateHand(ref left, p);
    UpdateHand(ref right, p);
  }

  void FixedUpdate() {
    IsNotNull(context);
    Pose p;
    if (!GetPose(out p)) return;

    if (left.grip) UpdateSnapToHand(ref left);
    if (right.grip) UpdateSnapToHand(ref right);
  }

  void UpdateHand(ref HandData d, Pose pose) {
    var hand = (d.id == LeftHand) ? pose.handLeftPose : pose.handRightPose;
    var controller = (d.id == LeftHand) ? pose.controllerLeftPose : pose.controllerRightPose;

    CollectInput(ref hand, ref controller, ref d.input); //why this is needed?
    UpdateGripFrame(ref d);
    CheckState(ref d);
    UpdateState(ref d);

    if (!d.grip) return;

    RotateGrip(ref d);
    ZoomGrip(ref d);
    UpdateHeldObj(ref d);
  }

  void RotateGrip(ref HandData d) {
    d.grip.transform.RotateAround(
      d.grip.transform.position,
      d.transform.forward,
      GetStickX(ref d) * RotateSpeed * deltaTime);
  }

  int GetStickX(ref HandData d) {
    if (d.input.stick.x <= -StickThreshold) return -1;
    if (d.input.stick.x >= StickThreshold) return 1;

    return 0;
  }

  void ZoomGrip(ref HandData d) {
    var grip = d.grip.transform;
    var position = GetHandPosition(ref d);
    var direction = grip.position - position;

    if (d.input.stick.y >= StickThreshold && Dot(direction, d.transform.forward) < ZoomMaximum)
      grip.position += ZoomSpeed * deltaTime * d.transform.forward; //zoom out: push out strictly along point direction. this lets objects grabbed up close always zoom out in a consistent direction

    else if (d.input.stick.y <= -StickThreshold && direction.magnitude > ZoomMinimum)
      grip.position = position + direction.normalized * Max(direction.magnitude - ZoomSpeed * deltaTime, ZoomMinimum); //zoom in: sneaky trick, pull center of mass towards hand on zoom in!
  }

  Vector3 GetHandPosition(ref HandData d) {
    var positions = new[] {
      new Vector3(-0.05f, 0.0f, 0.0f),
      new Vector3(+0.05f, 0.0f, 0.0f)
    };

    return d.transform.TransformPoint(positions[d.id]);
  }

  void UpdateSnapToHand(ref HandData d) {
    Vector3 start, direction;
    GetFingerInput(ref d, out start, out direction);

    if (d.input.indexTrigger < IndexThreshold
      || d.input.indexPressFrame + IndexStickyFrames < context.GetRenderFrame()
    ) return;

    d.input.indexPressFrame = 0;
    var delta = d.grip.transform.position - start; //warp to hand on index grip
    var distance = delta.magnitude;

    if (distance > WarpDistance) {
      distance = WarpDistance;

      if (distance < ZoomMinimum)
        distance = ZoomMinimum;

      var rigidBody = d.grip.GetComponent<Rigidbody>();
      var network = d.grip.GetComponent<NetworkInfo>();
      network.SmoothMove(start + delta, rigidBody.rotation);
    }

    for (int i = 0; i < ThrowRingBufferSize; ++i) { //clear the throw ring buffer
      d.throws[i].valid = false;
      d.throws[i].speed = 0.0f;
    }
  }

  void UpdateGripFrame(ref HandData d) {
    if (d.input.handTrigger <= GripThreshold)
      d.inputFrame = 0;

    else if (d.inputFrame == 0)
      d.inputFrame = context.GetRenderFrame();
  }

  void UpdateHeldObj(ref HandData d) {
    int id = (d.throwId++) % ThrowRingBufferSize; //track data to improve throw release in ring buffer
    var diff = d.grip.transform.position - d.prevGripPosition;
    var speed = (float)Sqrt(diff.x * diff.x + diff.z * diff.z);

    d.throws[id].valid = true;
    d.throws[id].speed = speed * RenderFrameRate;
    d.prevHandPosition = d.transform.position; //track previous positions and rotations for hand and index finger so we can use this to determine linear and angular velocity at time of release
    d.prevHandRotation = d.transform.rotation;
    d.prevGripPosition = d.grip.transform.position;
    d.prevGripRotation = d.grip.transform.rotation;

    var network = d.grip.GetComponent<NetworkInfo>(); //while an object is held set its last interaction frame to the current sim frame. this is used to boost priority for this object when it is thrown.
    network.SetInteractionFrame((long)context.GetSimulationFrame());
  }

  void CollectInput(ref HandPose hand, ref ControllerPose controller, ref HandInput i) {
    i.handTrigger = hand.gripFlex;
    i.previousIndexTrigger = i.indexTrigger;
    i.indexTrigger = hand.indexFlex;

    if (i.indexTrigger >= IndexThreshold && i.previousIndexTrigger < IndexThreshold)
      i.indexPressFrame = context.GetRenderFrame();

    i.isPointing = true;
    i.isPressingX = controller.button1IsDown;
    i.isPressingY = controller.button2IsDown;
    i.stick = controller.joystickPosition;
  }

  void CheckState(ref HandData d) {
    if (d.state == Neutral) {
      if (DetectGrip(ref d)) return;

      if (d.input.isPointing) {
        Transition(ref d, Pointing);
        return;
      }

    } else if(d.state == Pointing) {
      if (DetectGrip(ref d)) return;

      if (!d.input.isPointing) {
        Transition(ref d, Neutral);
        return;
      }

    } else if (d.state == Grip) {
      if (d.input.handTrigger >= GripThreshold) return;

      if (d.input.isPointing)
        Transition(ref d, Pointing);
      else
        Transition(ref d, Neutral);
    }
  }

  bool DetectGrip(ref HandData d) {
    if (d.state == Grip && d.grip == null) { //when it will execute?
      Transition(ref d, Neutral);
      return true;
    }

    if (d.input.handTrigger < GripThreshold) return false;
    if (d.point || d.pointFrame + PointStickyFrames < context.GetRenderFrame()) return false;

    var network = d.point.GetComponent<NetworkInfo>();
    if (!network.CanGrabCube(d.inputFrame)) return false;

    AttachToHand(ref d, d.point);
    Transition(ref d, Grip);

    return true;
  }

  void AttachToHand(ref HandData d, GameObject obj) {
    var network = obj.GetComponent<NetworkInfo>();
    network.AttachCubeToLocalPlayer(this, d);

#if DEBUG_AUTHORITY
        Debug.Log( "client " + context.GetClientIndex() + " grabbed cube " + network.GetCubeId() + " and set ownership sequence to " + network.GetOwnershipSequence() );
#endif // #if DEBUG_AUTHORITY

    if (!context.IsServer())
      network.ClearConfirmed();
    else
      network.SetConfirmed();

    for (int i = 0; i < ThrowRingBufferSize; ++i) {
      d.throws[i].valid = false;
      d.throws[i].speed = 0.0f;
    }
  }

  bool IsCloseGrip(ref HandData d) {
    if (!d.grip) return false;

    var delta = d.grip.transform.position - d.transform.position;

    return delta.magnitude <= GrabDistance;
  }

  bool IsThrowing(ref HandData d) {
    int count = 0;
    var sum = 0.0f;

    for (int i = 0; i < ThrowRingBufferSize; ++i) {
      if (d.throws[i].valid)
        sum += d.throws[i].speed;

      count++;
    }
    if (count == 0) return false;

    return sum/count >= ThrowSpeed;
  }

  void ApplyReleaseVelocity(ref HandData d, Rigidbody r, bool disableReleaseVelocity = false) {
    if (disableReleaseVelocity) {
      r.velocity = zero;
      r.angularVelocity = zero;

    } else if (IsCloseGrip(ref d) || IsThrowing(ref d)) {     
      r.velocity = (d.grip.transform.position - d.prevGripPosition) * RenderFrameRate; //throw mode
      r.angularVelocity = CalculateAngularVelocity(d.prevGripRotation, d.grip.transform.rotation, 1.0f / RenderFrameRate, 0.001f);

      if (r.velocity.magnitude > MaxThrowSpeed)
        r.velocity = (r.velocity / r.velocity.magnitude) * MaxThrowSpeed;

      if (r.velocity.x * r.velocity.x + r.velocity.z * r.velocity.z > HardThrowSpeed
        && r.velocity.y < ThrowVelocityMinY
      ) r.velocity = new Vector3(r.velocity.x, ThrowVelocityMinY, r.velocity.z);

    } else {        
      r.velocity = 3 * (d.transform.position - d.prevHandPosition) * RenderFrameRate; //placement mode
      r.angularVelocity = 2 * CalculateAngularVelocity(d.prevHandRotation, d.transform.rotation, 1.0f / RenderFrameRate, 0.1f);
    }
  }

  void WakeUpObjects(List<GameObject> objects) {
    foreach (var obj in objects) {
      var network = obj.GetComponent<NetworkInfo>();
      context.ResetBuffer(network.GetCubeId());

      if (network.GetAuthorityId() == 0)
        context.TakeAuthority(network);

      obj.GetComponent<Rigidbody>().WakeUp();
    }
  }

  void DetachFromHand(ref HandData d) {
    if (d.grip == null) return; //IMPORTANT: This happens when passing a cube from hand-to-hand

    var network = d.grip.GetComponent<NetworkInfo>();
    network.DetachCube();

#if DEBUG_AUTHORITY
        Debug.Log( "client " + context.GetClientIndex() + " released cube " + network.GetCubeId() + ". ownership sequence is " + network.GetOwnershipSequence() + ", authority sequence is " + network.GetAuthoritySequence() );
#endif // #if DEBUG_AUTHORITY
  }

  void Transition(ref HandData h, HandState state) {
    ExitState(ref h);
    EnterState(ref h, state);
  }

  void ExitState(ref HandData d) {
    if (d.state == Pointing)
      DestroyLine(ref d);

    else if (d.state == Grip)
      DetachFromHand(ref d);
  }

  void EnterState(ref HandData d, HandState state) {
    if (state == Pointing)
      CreatePointingLine(ref d);

    d.state = state;
  }

  void UpdateState(ref HandData d) {
    if (d.state == Pointing) {
      UpdateLine(ref d);
      ForcePointAnimation(ref d);

    } else if (d.state == Grip) {
      if (IsCloseGrip(ref d))
        ForceGripAnimation(ref d);
      else
        ForcePointAnimation(ref d);

      if (!d.grip) return;
      if (d.grip.transform.position.y > 0.0f) return;

      var position = d.grip.transform.position;
      position.y = 0.0f;
      d.grip.transform.position = position;
    }
  }

  void SetPointObject(ref HandData d, GameObject gameObject) {
    d.point = gameObject;
    d.pointFrame = context.GetRenderFrame();
  }

  void CreatePointingLine(ref HandData d) {
    if (d.line) return;

    d.line = Instantiate(linePrefab, zero, identity);
    IsNotNull(d.line);
  }

  bool FilterPointObject(ref HandData hand, Rigidbody r) {
    if (!r) return false;

    var obj = r.gameObject;
    if (!obj) return false;

    if (obj.layer != cubesLayer && obj.layer != gripLayer)
      return false;

    var network = obj.GetComponent<NetworkInfo>();
    if (!network) return false;

    return true;
  }

  void GetFingerInput(ref HandData d, out Vector3 start, out Vector3 direction) {
    start = GetHandPosition(ref d);
    direction = d.transform.forward;
  }

  void UpdateLine(ref HandData d) {
    IsNotNull(d.line);
    Vector3 start, direction;
    GetFingerInput(ref d, out start, out direction);

    var finish = start + direction * RaycastDistance;
    
    if (d.releaseFrame + PostReleaseDisableSelectFrames < context.GetRenderFrame()) { //don't allow any selection for a few frames after releasing an object      
      var colliders = Physics.OverlapSphere(d.transform.position, GrabRadius, layerMask); //first select any object overlapping the hand

      if (colliders.Length > 0 && FilterPointObject(ref d, colliders[0].attachedRigidbody)) {
        finish = start;
        SetPointObject(ref d, colliders[0].gameObject);
        d.isTouching = true;

      } else {       
        d.isTouching = false; //otherwise, raycast forward along point direction for accurate selection up close
        RaycastHit hit;

        if (Physics.Linecast(start, finish, out hit, layerMask)) {
          if (FilterPointObject(ref d, hit.rigidbody)) {
            finish = start + direction * hit.distance;
            SetPointObject(ref d, hit.rigidbody.gameObject);
          }

        } else if (Physics.SphereCast(start + direction * PointSphereCastStart, PointSphereCastRadius, finish, out hit, layerMask)) {
          // failing an accurate hit, sphere cast starting from a bit further away to provide easier selection of far away objects
          if (FilterPointObject(ref d, hit.rigidbody)) {
            finish = start + direction * (PointSphereCastStart + hit.distance);
            SetPointObject(ref d, hit.rigidbody.gameObject);
          }
        }
      }
    }

    var line = d.line.GetComponent<LineRenderer>();
    if (!line) return;

    line.positionCount = 2;
    line.SetPosition(0, start);
    line.SetPosition(1, finish);
    line.startWidth = LineWidth;
    line.endWidth = LineWidth;
  }

  void DestroyLine(ref HandData d) {
    IsNotNull(d.line);
    Destroy(d.line);
    d.line = null;
  }

  void ForcePointAnimation(ref HandData d) {
    if (d.isTouching && d.state == Pointing)
      d.animator.SetLayerWeight(d.animator.GetLayerIndex("Point Layer"), 0.0f); //indicates state of touching an object to player (for up-close grip)
    else
      d.animator.SetLayerWeight(d.animator.GetLayerIndex("Point Layer"), 1.0f);

    d.animator.SetLayerWeight(d.animator.GetLayerIndex("Thumb Layer"), 0.0f);
  }

  void ForceGripAnimation(ref HandData h) {
    h.animator.SetLayerWeight(h.animator.GetLayerIndex("Point Layer"), 0.0f);
    h.animator.SetLayerWeight(h.animator.GetLayerIndex("Thumb Layer"), 0.0f);
  }

  Vector3 CalculateAngularVelocity(Quaternion previous, Quaternion current, float delta, float minimumAngle) {
    IsTrue(delta > 0.0f);
    var rotation = current * Inverse(previous);
    var angle = (float)(2.0f * Acos(rotation.w));

    if (float.IsNaN(angle)) return zero;
    if (Abs(angle) < minimumAngle) return zero;

    if (angle > PI)
      angle -= 2.0f * (float)PI;

    var cone = (float)Sqrt(rotation.x * rotation.x
      + rotation.y * rotation.y
      + rotation.z * rotation.z);

    var speed = angle / delta / cone;

    var velocity = new Vector3(
      speed * rotation.x, 
      speed * rotation.y, 
      speed * rotation.z);

    IsFalse(float.IsNaN(velocity.x));
    IsFalse(float.IsNaN(velocity.y));
    IsFalse(float.IsNaN(velocity.z));

    return velocity;
  }

  public bool GetState(out AvatarState s) {
    Pose pose;
    if (!avatar.Driver.GetPose(out pose)) {
      s = AvatarState.Default;
      return false;
    }
    AvatarState.Initialize(out s, context.GetClientId(), pose, left.grip, right.grip);

    return true;
  }

  public void AttachCube(ref HandData d) => UpdateHeldObj(ref d);

  public void DetachCube(ref HandData d) {
    var rigidBody = d.grip.GetComponent<Rigidbody>();
    rigidBody.isKinematic = false;
    d.grip.layer = cubesLayer;
    ApplyReleaseVelocity(ref d, rigidBody, d.hasReleaseVelocity);
    d.grip.transform.SetParent(null);
    d.grip = null;

    if (rigidBody.position.y < MinimumCubeHeight)
      rigidBody.position = rigidBody.position.WithY(MinimumCubeHeight);

    if (d.supports != null) {
      WakeUpObjects(d.supports);
      d.supports = null;
    }

    d.point = null;
    d.pointFrame = 0;
    d.releaseFrame = context.GetRenderFrame();
  }

  public void ResetHand(ref HandData d) {
    d.hasReleaseVelocity = true;
    Transition(ref d, Neutral);
    d.hasReleaseVelocity = false;
    d.isTouching = false;
    d.line = null;
    d.point = null;
    d.pointFrame = 0;
    d.grip = null;
    d.inputFrame = 0;
    d.startFrame = 0;
    d.prevHandPosition = zero;
    d.prevGripPosition = zero;
    d.prevHandRotation = identity;
    d.prevGripRotation = identity;
    d.releaseFrame = 0;
    d.supports = null;
  }

  public bool IsPressingGrip() => left.input.handTrigger > GripThreshold || right.input.handTrigger > GripThreshold;
  public bool IsPressingIndex() => left.input.indexTrigger > IndexThreshold || right.input.indexTrigger > IndexThreshold;
  public bool IsPressingX() => left.input.isPressingX || right.input.isPressingX;
  public bool IsPressingY() => left.input.isPressingY || right.input.isPressingY;
  public override bool GetPose(out Pose p) => base.GetPose(out p);
}