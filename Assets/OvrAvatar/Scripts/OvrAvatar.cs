using UnityEngine;
using System;
using Pose = OvrAvatarDriver.Pose;

/// <summary>
/// Updates position, rotation, hands, controllers and head view
/// </summary>
public class OvrAvatar : MonoBehaviour {
  public class PacketEventArgs : EventArgs {
    public readonly OvrAvatarPacket Packet;

    public PacketEventArgs(OvrAvatarPacket packet) {
      Packet = packet;
    }
  }

  public EventHandler<PacketEventArgs> PacketRecorded;
  public OvrAvatarDriver Driver;
  public Transform HeadRoot;
  public OvrAvatarHead Head;
  public Transform HandLeftRoot;
  public Transform HandRightRoot;
  public OvrAvatarTouchController ControllerLeft;
  public OvrAvatarTouchController ControllerRight;
  public OvrAvatarHand HandLeft;
  public OvrAvatarHand HandRight;
  public bool RecordPackets;
  public bool StartWithControllers;
  public bool TrackPositions = true;
  public bool TrackRotations = true;
  const float PacketDurationSec = 1 / 30.0f;
  OvrAvatarPacket packet;

  void Start() => ShowControllers(StartWithControllers);

  void Update() {
    if (Driver == null) return;

    Pose p; //Get the current pose from the driver
    if (!Driver.GetPose(out p)) return;
    
    if (RecordPackets)
      RecordPose(Time.deltaTime, p); //If we're recording, record the pose

    UpdateTransform(HeadRoot, p.headPosition, p.headRotation); //Update the various avatar components with this pose
    UpdateTransform(HandLeftRoot, p.handLeftPosition, p.handLeftRotation);
    UpdateTransform(HandRightRoot, p.handRightPosition, p.handRightRotation);
    ControllerLeft?.UpdatePose(p.controllerLeftPose);
    ControllerRight?.UpdatePose(p.controllerRightPose);
    HandLeft?.UpdatePose(p.handLeftPose);
    HandRight?.UpdatePose(p.handRightPose);
    Head?.UpdatePose(p.voiceAmplitude);
    TempFixupTransforms();
  }

  void TempFixupTransforms() {    
    if (ControllerLeft != null && HandLeft != null) { //If we're showing controllers, fix up the hand transforms to center the grip around the controller
      if (ControllerLeft.gameObject.activeSelf) {
        HandLeft.transform.localPosition = new Vector3(0.0088477f, -0.013713f, -0.006552f);
        HandLeft.transform.localRotation = Quaternion.Euler(1.44281f, 2.976443f, 349.8051f);
      } else {
        HandLeft.transform.localPosition = Vector3.zero;
        HandLeft.transform.localRotation = Quaternion.identity;
      }
    }

    if (ControllerRight != null && HandRight != null) {
      if (ControllerRight.gameObject.activeSelf) {
        HandRight.transform.localPosition = new Vector3(-0.0088477f, -0.013713f, -0.006552f);
        HandRight.transform.localRotation = Quaternion.Euler(-1.44281f, 2.976443f, -349.8051f);
      } else {
        HandRight.transform.localPosition = Vector3.zero;
        HandRight.transform.localRotation = Quaternion.identity;
      }
    }
  }

  public void ShowControllers(bool show) {
    HandLeft?.HoldController(show);
    HandRight?.HoldController(show);
    ControllerLeft?.gameObject.SetActive(show);
    ControllerRight?.gameObject.SetActive(show);
  }

  void UpdateTransform(Transform t, Vector3 position, Quaternion rotation) {
    if (t == null) return;

    if (TrackPositions) t.localPosition = position;
    if (TrackRotations) t.localRotation = rotation;
  }

  void RecordPose(float delta, Pose p) {    
    if (packet == null) { //If this is our first packet, store the pose as the initial frame
      packet = new OvrAvatarPacket(p);
      delta = 0;
    }

    var recorded = 0f;
    while (recorded < delta) {
      var left = delta - recorded;
      var inPacket = PacketDurationSec - packet.LastTime; //what if it's negative?
      
      if (left < inPacket) { //If we're not going to fill the packet, just add the frame
        packet.AddFrame(p, left);
        recorded += left;
      } else {
        // If we're going to fill the packet, interpolate the pose, send the packet,
        // and open a new one
        // Interpolate between the packet's last frame and our target pose
        // to compute a pose at the end of the packet time.
        var pose = Pose.Interpolate(packet.LastFrame, p, inPacket / left);
        packet.AddFrame(pose, inPacket);
        recorded += inPacket;
        PacketRecorded?.Invoke(this, new PacketEventArgs(packet)); //Broadcast the recorded packet
        packet = new OvrAvatarPacket(pose); //Open a new packet
      }
    }
  }
}     