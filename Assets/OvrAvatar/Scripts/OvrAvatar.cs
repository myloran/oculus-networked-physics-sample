using UnityEngine;
using System;
using Frame = OvrAvatarDriver.PoseFrame;

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

  void Start() {
    ShowControllers(StartWithControllers);
  }

  void Update() {
    if (Driver == null) return;

    Frame pose; //Get the current pose from the driver
    if (!Driver.GetCurrentPose(out pose)) return;
    
    if (RecordPackets) RecordFrame(Time.deltaTime, pose); //If we're recording, record the pose

    UpdateTransform(HeadRoot, pose.headPosition, pose.headRotation); //Update the various avatar components with this pose
    UpdateTransform(HandLeftRoot, pose.handLeftPosition, pose.handLeftRotation);
    UpdateTransform(HandRightRoot, pose.handRightPosition, pose.handRightRotation);

    ControllerLeft?.UpdatePose(pose.controllerLeftPose);
    ControllerRight?.UpdatePose(pose.controllerRightPose);
    HandLeft?.UpdatePose(pose.handLeftPose);
    HandRight?.UpdatePose(pose.handRightPose);
    Head?.UpdatePose(pose.voiceAmplitude);
    TempFixupTransforms();
  }

  void TempFixupTransforms() {
    // If we're showing controllers, fix up the hand transforms to center the grip around the controller
    if (ControllerLeft != null && HandLeft != null) {
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

  void UpdateTransform(Transform transform, Vector3 position, Quaternion rotation) {
    if (transform == null) return;

    if (TrackPositions) transform.localPosition = position;
    if (TrackRotations) transform.localRotation = rotation;
  }

  void RecordFrame(float delta, Frame frame) {    
    if (packet == null) { //If this is our first packet, store the pose as the initial frame
      packet = new OvrAvatarPacket(frame);
      delta = 0;
    }

    var recorded = 0f;
    while (recorded < delta) {
      var left = delta - recorded;
      var inPacket = PacketDurationSec - packet.LastTime;
      
      if (left < inPacket) { //If we're not going to fill the packet, just add the frame
        packet.AddFrame(frame, left);
        recorded += left;
      } else {
        // If we're going to fill the packet, interpolate the pose, send the packet,
        // and open a new one
        // Interpolate between the packet's last frame and our target pose
        // to compute a pose at the end of the packet time.
        var pose = Frame.Interpolate(packet.LastFrame, frame, inPacket / left);
        packet.AddFrame(pose, inPacket);
        recorded += inPacket;
        PacketRecorded?.Invoke(this, new PacketEventArgs(packet)); //Broadcast the recorded packet
        packet = new OvrAvatarPacket(pose); //Open a new packet
      }
    }
  }
}     