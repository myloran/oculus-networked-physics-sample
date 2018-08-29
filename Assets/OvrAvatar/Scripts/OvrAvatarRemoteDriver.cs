using UnityEngine;
using System.Collections.Generic;

public class OvrAvatarRemoteDriver : OvrAvatarDriver {
  Queue<OvrAvatarPacket> packetQueue = new Queue<OvrAvatarPacket>();
  OvrAvatarPacket currentPacket = null;
  PoseFrame currentPose = null;
  const int MinPacketQueue = 1;
  const int MaxPacketQueue = 4;
  float currentPacketTime = 0.0f;
  bool isStreaming = false;
  int currentSequence = -1;

  void Update() {
    if (!isStreaming && packetQueue.Count > MinPacketQueue) { //If we're not currently streaming, check to see if we've buffered enough
      currentPacket = packetQueue.Dequeue();
      isStreaming = true;
    }

    if (isStreaming) { //If we are streaming, update our pose
      currentPacketTime += Time.deltaTime;
            
      while (currentPacketTime > currentPacket.LastTime) { //If we've elapsed past our current packet, advance
        if (packetQueue.Count == 0) { //If we're out of packets, stop streaming and lock to the final frame
          currentPose = currentPacket.LastFrame;
          currentPacketTime = 0.0f;
          currentPacket = null;
          isStreaming = false;
          return;
        }

        while (packetQueue.Count > MaxPacketQueue)
          packetQueue.Dequeue();
                
        currentPacketTime -= currentPacket.LastTime; //Otherwise, dequeue the next packet
        currentPacket = packetQueue.Dequeue();
      }
            
      currentPose = currentPacket.GetPoseFrame(currentPacketTime); //Compute the pose based on our current time offset in the packet
    }
  }

  public void QueuePacket(int sequence, OvrAvatarPacket packet) {
    if (sequence - currentSequence < 0) return;

    currentSequence = sequence;
    packetQueue.Enqueue(packet);
  }

  public override bool GetCurrentPose(out PoseFrame pose) {
    if (currentPose != null) {
      pose = currentPose;
      return true;
    }
    pose = null;
    return false;
  }
}