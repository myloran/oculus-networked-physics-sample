using UnityEngine;
using System.IO;
using System.Collections.Generic;
using Frame = OvrAvatarDriver.PoseFrame;
using Controller = OvrAvatarDriver.ControllerPose;
using Hand = OvrAvatarDriver.HandPose;

public class OvrAvatarPacket {  
  public Frame LastFrame { get { return frames[frames.Count - 1]; } }
  public float LastTime { get { return times[times.Count - 1]; } }
  List<Frame> frames = new List<Frame>();
  List<float> times = new List<float>();
  List<byte[]> audios = new List<byte[]>(); //encodedAudioPackets

  public OvrAvatarPacket(Frame initialPose) {
    times.Add(0.0f);
    frames.Add(initialPose);
  }

  OvrAvatarPacket(List<float> times, List<Frame> frames, List<byte[]> audioPackets) {
    this.times = times;
    this.frames = frames;
  }

  public void AddFrame(Frame frame, float delta) {
    times.Add(LastTime + delta);
    frames.Add(frame);
  }

  public Frame GetPoseFrame(float seconds) {
    if (frames.Count == 1) return frames[0];
    
    int index = 1; //This can be replaced with a more efficient binary search
    while (index < times.Count && times[index] < seconds)
      ++index;
    
    var from = times[index - 1];
    var to = times[index];
    var time = (seconds-from) / (to-from);

    return Frame.Interpolate(frames[index - 1], frames[index], time);
  }

  public static OvrAvatarPacket Read(Stream stream) {
    var r = new BinaryReader(stream);
    var frameCount = r.ReadInt32(); //Todo: bounds check frame count
    var times = new List<float>(frameCount);
    var frames = new List<Frame>(frameCount);

    for (int i = 0; i < frameCount; ++i)
      times.Add(r.ReadSingle());

    for (int i = 0; i < frameCount; ++i)
      frames.Add(r.ReadPoseFrame());
        
    var audioCount = r.ReadInt32(); //Todo: bounds check audio packet count
    var audios = new List<byte[]>(audioCount);

    for (int i = 0; i < audioCount; ++i) {
      var size = r.ReadInt32();
      var packet = r.ReadBytes(size);
      audios.Add(packet);
    }

    return new OvrAvatarPacket(times, frames, audios);
  }

  public void Write(Stream stream) {
    var w = new BinaryWriter(stream);
    
    w.Write(times.Count); //Write all of the frames

    for (int i = 0; i < times.Count; ++i)
      w.Write(times[i]);

    for (int i = 0; i < times.Count; ++i)
      w.Write(frames[i]);
        
    w.Write(audios.Count); //Write all of the encoded audio packets

    for (int i = 0; i < audios.Count; ++i) {
      w.Write(audios[i].Length);
      w.Write(audios[i]);
    }
  }
}

static class BinaryWriterExtensions {
  public static void Write(this BinaryWriter w, Frame frame) {
    w.Write(frame.headPosition);
    w.Write(frame.headRotation);
    w.Write(frame.handLeftPosition);
    w.Write(frame.handLeftRotation);
    w.Write(frame.handRightPosition);
    w.Write(frame.handRightRotation);
    w.Write(frame.voiceAmplitude);
    w.Write(frame.controllerLeftPose);
    w.Write(frame.controllerRightPose);
    w.Write(frame.handLeftPose);
    w.Write(frame.handRightPose);
  }

  public static void Write(this BinaryWriter w, Vector3 vec3) {
    w.Write(vec3.x);
    w.Write(vec3.y);
    w.Write(vec3.z);
  }

  public static void Write(this BinaryWriter w, Vector2 vec2) {
    w.Write(vec2.x);
    w.Write(vec2.y);
  }

  public static void Write(this BinaryWriter w, Quaternion quat) {
    w.Write(quat.x);
    w.Write(quat.y);
    w.Write(quat.z);
    w.Write(quat.w);
  }
  public static void Write(this BinaryWriter w, Controller pose) {
    w.Write(pose.button1IsDown);
    w.Write(pose.button2IsDown);
    w.Write(pose.joystickPosition);
    w.Write(pose.indexTrigger);
    w.Write(pose.gripTrigger);
  }

  public static void Write(this BinaryWriter w, Hand pose) {
    w.Write(pose.indexFlex);
    w.Write(pose.gripFlex);
    w.Write(pose.isPointing);
    w.Write(pose.isThumbUp);
  }
}

static class BinaryreaderExtensions {
  public static Frame ReadPoseFrame(this BinaryReader r) {
    return new Frame {
      headPosition = r.ReadVector3(),
      headRotation = r.ReadQuaternion(),
      handLeftPosition = r.ReadVector3(),
      handLeftRotation = r.ReadQuaternion(),
      handRightPosition = r.ReadVector3(),
      handRightRotation = r.ReadQuaternion(),
      voiceAmplitude = r.ReadSingle(),
      controllerLeftPose = r.ReadControllerPose(),
      controllerRightPose = r.ReadControllerPose(),
      handLeftPose = r.ReadHandPose(),
      handRightPose = r.ReadHandPose()
    };
  }

  public static Vector2 ReadVector2(this BinaryReader r) {
    return new Vector2 {
      x = r.ReadSingle(),
      y = r.ReadSingle()
    };
  }

  public static Vector3 ReadVector3(this BinaryReader r) {
    return new Vector3 {
      x = r.ReadSingle(),
      y = r.ReadSingle(),
      z = r.ReadSingle()
    };
  }

  public static Quaternion ReadQuaternion(this BinaryReader r) {
    return new Quaternion {
      x = r.ReadSingle(),
      y = r.ReadSingle(),
      z = r.ReadSingle(),
      w = r.ReadSingle(),
    };
  }
  public static Controller ReadControllerPose(this BinaryReader r) {
    return new Controller {
      button1IsDown = r.ReadBoolean(),
      button2IsDown = r.ReadBoolean(),
      joystickPosition = r.ReadVector2(),
      indexTrigger = r.ReadSingle(),
      gripTrigger = r.ReadSingle(),
    };
  }

  public static Hand ReadHandPose(this BinaryReader r) {
    return new Hand {
      indexFlex = r.ReadSingle(),
      gripFlex = r.ReadSingle(),
      isPointing = r.ReadBoolean(),
      isThumbUp = r.ReadBoolean()
    };
  }
}