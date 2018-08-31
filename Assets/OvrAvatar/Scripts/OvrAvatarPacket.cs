using UnityEngine;
using System.IO;
using System.Collections.Generic;
using Frame = OvrAvatarDriver.Pose;
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
    
    int id = 1; //This can be replaced with a more efficient binary search
    while (id < times.Count && times[id] < seconds)
      ++id;
    
    var from = times[id - 1];
    var to = times[id];
    var time = (seconds-from) / (to-from);

    return Frame.Interpolate(frames[id - 1], frames[id], time);
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
  public static void Write(this BinaryWriter w, Frame f) {
    w.Write(f.headPosition);
    w.Write(f.headRotation);
    w.Write(f.handLeftPosition);
    w.Write(f.handLeftRotation);
    w.Write(f.handRightPosition);
    w.Write(f.handRightRotation);
    w.Write(f.voiceAmplitude);
    w.Write(f.controllerLeftPose);
    w.Write(f.controllerRightPose);
    w.Write(f.handLeftPose);
    w.Write(f.handRightPose);
  }

  public static void Write(this BinaryWriter w, Vector3 v) {
    w.Write(v.x);
    w.Write(v.y);
    w.Write(v.z);
  }

  public static void Write(this BinaryWriter w, Vector2 v) {
    w.Write(v.x);
    w.Write(v.y);
  }

  public static void Write(this BinaryWriter w, Quaternion q) {
    w.Write(q.x);
    w.Write(q.y);
    w.Write(q.z);
    w.Write(q.w);
  }
  public static void Write(this BinaryWriter w, Controller c) {
    w.Write(c.button1IsDown);
    w.Write(c.button2IsDown);
    w.Write(c.joystickPosition);
    w.Write(c.indexTrigger);
    w.Write(c.gripTrigger);
  }

  public static void Write(this BinaryWriter w, Hand p) {
    w.Write(p.indexFlex);
    w.Write(p.gripFlex);
    w.Write(p.isPointing);
    w.Write(p.isThumbUp);
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