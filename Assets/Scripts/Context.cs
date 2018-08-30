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
using UnityEngine.Profiling;
using UnityEngine.Assertions;
using System.Collections.Generic;
using System.Linq;
using Network;
using System.IO;
using static Constants;

public class Context : MonoBehaviour {
  public GameObject[] 
    remoteAvatar = new GameObject[MaxClients],
    remoteLinePrefabs = new GameObject[MaxClients];

  public Material[] authorityMaterials = new Material[MaxAuthority];
  public GameObject cubePrefab;

  ConnectionData[] serverData;
  ConnectionData clientData;
  Interactions interactions = new Interactions();
  HashSet<int> visited = new HashSet<int>();
  Snapshot snapshot = new Snapshot();
  GameObject[] cubes = new GameObject[NumCubes];
  Vector3[] cubePositions = new Vector3[NumCubes];
  RingBuffer[] buffer = new RingBuffer[NumCubes * RingBufferSize];

  ulong[] collisionFrames = new ulong[NumCubes];

  ulong renderFrame = 0,
    simulationFrame = 0;

  int 
    clientId,
    authorityId,
    layer;

  bool isActive = true;
  ushort resetSequence = 0;

  public struct Priority {
    public int cubeId;
    public float accumulator;
  };

  public struct Acks {
    public bool isAcked;
    public ushort sequence;
    public ushort resetSequence;
    public CubeState state;
  };

  struct RingBuffer {
    public Vector3 position;
    public Vector3 axis;
  };

  public class ConnectionData {
    public Connection connection = new Connection();
    public DeltaBuffer sendBuffer = new DeltaBuffer(DeltaBufferSize);
    public DeltaBuffer receiveBuffer = new DeltaBuffer(DeltaBufferSize);
    public JitterBuffer jitterBuffer = new JitterBuffer();
    public Priority[] priorities = new Priority[NumCubes];
    public Acks[] acks = new Acks[NumCubes];
    public bool isFirstPacket = true;
    public long frame = -1;

    public ConnectionData() {
      Reset();
    }

    public void Reset() {
      Profiler.BeginSample("ConnectionData.Reset");
      connection.Reset();
      sendBuffer.Reset();
      receiveBuffer.Reset();

      for (int i = 0; i < priorities.Length; ++i) {
        priorities[i].cubeId = i;
        priorities[i].accumulator = 0.0f;
      }

      for (int i = 0; i < acks.Length; ++i) {
        acks[i].isAcked = false;
        acks[i].sequence = 0;
        acks[i].resetSequence = 0;
      }

      isFirstPacket = true;
      frame = -1;
      jitterBuffer.Reset();
      Profiler.EndSample();
    }
  };

  public ConnectionData GetClientData() {
    Assert.IsTrue(IsClient());

    return clientData;
  }

  public ConnectionData GetServerData(int id) {
    Assert.IsTrue(IsServer());
    Assert.IsTrue(id >= 1);
    Assert.IsTrue(id <= MaxClients);

    return serverData[id - 1];
  }

  public void Init(int id) {
    clientId = id;
    authorityId = id + 1;
    Assert.IsTrue(clientId >= 0 && clientId < MaxClients);
    Assert.IsTrue(authorityId >= 0 && authorityId < MaxAuthority);

    if (id == 0) {      
      clientData = null; //initialize as server
      serverData = new ConnectionData[MaxClients - 1];

      for (int i = 0; i < serverData.Length; ++i) {
        serverData[i] = new ConnectionData();
        InitPriorities(serverData[i]);
      }
    } else {      
      clientData = new ConnectionData(); //initialize as client
      serverData = null;
      InitPriorities(clientData);
    }
  }

  public void Shutdown() {
    clientId = 0;
    authorityId = 0;
    clientData = null;
    serverData = null;
  }

  public void Activate() {
    isActive = true;
    FreezeCubes(false);
    ShowContext(true);
  }

  public void Deactivate() {
    isActive = false;
    FreezeCubes(true);
    ShowContext(false);
  }

  public bool IsActive() => isActive;
  public bool IsServer() => IsActive() && clientId == 0;
  public bool IsClient() => IsActive() && clientId != 0;
  public int GetLayer() => layer;
  public int GetGripLayer() => layer + 1;
  public int GetTouchingLayer() => layer + 2;
  public int GetClientId() => clientId;
  public int GetAuthorityId() => authorityId;

  public RemoteAvatar GetAvatar(int id) {
    Assert.IsTrue(id >= 0);
    Assert.IsTrue(id < MaxClients);
    
    return remoteAvatar[id]?.GetComponent<RemoteAvatar>();
  }

  void ShowObj(GameObject obj, bool show) {
    foreach (var renderer in obj.GetComponentsInChildren<Renderer>())
      renderer.enabled = show;
  }

  public void ShowAvatar(int id) => ShowObj(GetAvatar(id).gameObject, true);
  public void HideAvatar(int id) => ShowObj(GetAvatar(id).gameObject, false);

  public void ResetAuthority(int clientId) {
    for (int i = 0; i < NumCubes; ++i) {
      var network = cubes[i].GetComponent<NetworkInfo>();
      if (network.GetAuthorityId() != clientId + 1) continue;

      Debug.Log("Returning cube " + i + " to default authority");
      network.DetachCube();
      network.SetAuthorityId(0);
      network.SetAuthoritySequence(0);
      network.IncreaseOwnershipSequence();
      var rigidBody = cubes[i].GetComponent<Rigidbody>();

      if (rigidBody.IsSleeping())
        rigidBody.WakeUp();

      ResetBuffer(i);
    }
  }

  public GameObject GetAvatarHead(int id) => GetAvatar(id)?.GetHead();
  public GameObject GetCube(int id) => cubes[id];
  public void IncreaseResetSequence() => resetSequence++;
  public void SetResetSequence(ushort sequence) => resetSequence = sequence;
  public ushort GetResetSequence() => resetSequence;
  public ulong GetRenderFrame() => renderFrame;
  public ulong GetSimulationFrame() => simulationFrame;

  public bool GetAck(ConnectionData d, int cubeId, ref ushort sequence, ushort resetSequence, ref CubeState state) {
    if (!d.acks[cubeId].isAcked) return false;
    if (d.acks[cubeId].resetSequence != resetSequence) return false;

    sequence = d.acks[cubeId].sequence;
    state = d.acks[cubeId].state;

    return true;
  }

  public bool UpdateAck(ConnectionData d, int cubeId, ushort sequence, ushort resetSequence, ref CubeState state) {
    if (d.acks[cubeId].isAcked
      && (Util.SequenceGreaterThan(d.acks[cubeId].resetSequence, resetSequence)
      || Util.SequenceGreaterThan(d.acks[cubeId].sequence, sequence))
    ) return false;

    d.acks[cubeId].isAcked = true;
    d.acks[cubeId].sequence = sequence;
    d.acks[cubeId].resetSequence = resetSequence;
    d.acks[cubeId].state = state;

    return true;
  }

  void Awake() {
    Assert.IsTrue(cubePrefab);
    layer = gameObject.layer;
    InitAvatars();
    InitCubePositions();
    CreateCubes();
  }

  public void FixedUpdate() {
    if (!IsActive()) return;

    ProcessInteractions();
    CaptureSnapshot(snapshot);
    ApplySnapshot(snapshot, true, true);
    AddStateToBuffer();
    UpdateAvatars();
    UpdateCubesAuthority();
    simulationFrame++;
  }

  public void Update() {
    if (!IsActive()) return;

    ProcessInteractions();
    Profiler.BeginSample("UpdateAuthorityMaterials");
    UpdateAuthorityMaterials();
    Profiler.EndSample();
    renderFrame++;
  }

  public void LateUpdate() => SmoothCubes();

  public void Reset() {
    Profiler.BeginSample("Reset");
    Assert.IsTrue(IsActive());
    CreateCubes();

    if (IsServer()) {
      for (int i = 1; i < MaxClients; ++i) {
        var data = GetServerData(i);
        data.sendBuffer.Reset();
        data.receiveBuffer.Reset();
      }
    } else {
      var data = GetClientData();
      data.sendBuffer.Reset();
      data.receiveBuffer.Reset();
    }
    Profiler.EndSample();
  }

  void UpdateAuthorityMaterials() {
    for (int i = 0; i < NumCubes; i++) {
      var network = cubes[i].GetComponent<NetworkInfo>();
      var renderer = network.smoothed.GetComponent<Renderer>();
      int id = network.GetAuthorityId();

      renderer.material.Lerp(
        renderer.material, 
        authorityMaterials[id], 
        id != 0 ? 0.3f : 0.04f);
    }
  }

  public Vector3 GetOrigin() => gameObject.transform.position;

  void CreateCubes() {
    Profiler.BeginSample("CreateCubes");

    for (int i = 0; i < NumCubes; i++) {
      if (!cubes[i]) {
        cubes[i] = Instantiate(cubePrefab, cubePositions[i] + GetOrigin(), Quaternion.identity); //cube initial create
        cubes[i].layer = gameObject.layer;
        var rigidBody = cubes[i].GetComponent<Rigidbody>();
        var network = cubes[i].GetComponent<NetworkInfo>();

        rigidBody.maxDepenetrationVelocity = PushOutVelocity; //this is *extremely* important to reduce jitter in the remote view of large stacks of rigid bodies
        network.touching.layer = GetTouchingLayer();
        network.Init(this, i);
      } else {        
        var rigidBody = cubes[i].GetComponent<Rigidbody>(); //cube already exists: force it back to initial state

        if (rigidBody.IsSleeping())
          rigidBody.WakeUp();

        rigidBody.position = cubePositions[i] + GetOrigin();
        rigidBody.rotation = Quaternion.identity;
        rigidBody.velocity = Vector3.zero;
        rigidBody.angularVelocity = Vector3.zero;
        ResetBuffer(i);

        var network = cubes[i].GetComponent<NetworkInfo>();
        network.DetachCube();
        network.SetAuthorityId(0);
        network.SetAuthoritySequence(0);
        network.SetOwnershipSequence(0);

        var renderer = network.smoothed.GetComponent<Renderer>();
        renderer.material = authorityMaterials[0];
        network.m_positionError = Vector3.zero;
        network.m_rotationError = Quaternion.identity;
        cubes[i].transform.parent = null;
      }
    }
    Profiler.EndSample();
  }

  void ShowContext(bool show) {
    ShowObj(gameObject, show);

    for (int i = 0; i < NumCubes; i++) {
      if (!cubes[i]) continue;

      var network = cubes[i].GetComponent<NetworkInfo>();
      network.smoothed.GetComponent<Renderer>().enabled = show;
    }
  }

  public void FreezeCubes(bool freeze) {
    for (int i = 0; i < NumCubes; i++) {
      if (!cubes[i]) continue;

      cubes[i].GetComponent<Rigidbody>().isKinematic = freeze;
    }
  }

  public void Collide(int cubeId1, int cubeId2, Collision collision) {
    if (collision.relativeVelocity.sqrMagnitude > HighEnergyCollisionThreshold * HighEnergyCollisionThreshold) {
      collisionFrames[cubeId1] = simulationFrame;

      if (cubeId2 != -1)
        collisionFrames[cubeId2] = simulationFrame;
    }
  }

  public void OnTouchStart(int cubeId1, int cubeId2) 
    => interactions.Add((ushort)cubeId1, (ushort)cubeId2);

  public void OnTouchFinish(int cubeId1, int cubeId2) 
    => interactions.Remove((ushort)cubeId1, (ushort)cubeId2);

  public void FindSupports(GameObject obj, ref HashSet<GameObject> supports) {
    if (supports.Contains(obj)) return;

    supports.Add(obj);
    int id = obj.GetComponent<NetworkInfo>().GetCubeId();

    for (int i = 0; i < NumCubes; ++i) {
      if (interactions.Get(id).interactions[i] == 0) continue;
      if (cubes[i].layer != layer) continue;
      if (cubes[i].transform.position.y < obj.transform.position.y + SupportHeightThreshold) continue;

      FindSupports(cubes[i], ref supports);
    }
  }

  public List<GameObject> GetSupports(GameObject obj) {
    // Support objects are used to determine the set of objects that should be woken up when you grab a cube.
    // Without this, objects resting on the cube you grab stay floating in the air. This function tries to only
    // wake up objects that are above (resting on) the game object that is being recursively walked. The idea being
    // if you grab an object in the middle of a stack, it wakes up any objects above it, but not below or to the side.
    var supports = new HashSet<GameObject>();
    FindSupports(obj, ref supports);

    return supports.ToList();
  }

  public void TakeAuthority(NetworkInfo n) {
    Assert.IsTrue(n.GetAuthorityId() == 0);
#if DEBUG_AUTHORITY
    Debug.Log( "client " + clientIndex + " took authority over cube " + n.GetCubeId() );
#endif // #if DEBUG_AUTHORITY
    n.SetAuthorityId(authorityId);
    n.IncreaseAuthoritySequence();

    if (!IsServer())
      n.ClearConfirmed();
    else
      n.SetConfirmed();
  }

  void ProcessInteractions(int cubeId) {
    if (visited.Contains(cubeId)) {
      Assert.IsTrue(cubes[cubeId].GetComponent<NetworkInfo>().GetAuthorityId() == authorityId);
      return;
    }

    visited.Add(cubeId);
    var entry = interactions.Get(cubeId);

    for (int i = 0; i < NumCubes; ++i) {
      if (entry.interactions[i] == 0) continue;

      var network = cubes[i].GetComponent<NetworkInfo>();
      if (network.GetAuthorityId() != 0) continue;

      TakeAuthority(network);
      ProcessInteractions(i);
    }
  }

  void ProcessInteractions() {
    Profiler.BeginSample("ProcessInteractions");
    visited.Clear();

    for (int i = 0; i < NumCubes; ++i) {
      var network = cubes[i].GetComponent<NetworkInfo>();
      if (network.GetAuthorityId() != authorityId) continue;
      if (network.IsHeldByPlayer()) continue;
      if (cubes[i].GetComponent<Rigidbody>().IsSleeping()) continue;

      ProcessInteractions((ushort)i);
    }
    Profiler.EndSample();
  }

  void UpdateAvatars() {
    Profiler.BeginSample("UpdateRemoteAvatars");

    for (int i = 0; i < MaxClients; ++i)
      GetAvatar(i)?.Update();

    Profiler.EndSample();
  }

  void UpdateCubesAuthority() {
    Profiler.BeginSample("UpdateCubeAuthority");
    /*
     * After objects have been at rest for some period of time they return to default authority (white).
     * This logic runs on the client that has authority over the object. To avoid race conditions where the 
     * first client to activate an object and put it to rest wins in case of a conflict, the client delays
     * returning an object to default authority until after it has received confirmation from the server that
     * it has authority over that object.
     */
    for (int i = 0; i < NumCubes; ++i) {
      var network = cubes[i].GetComponent<NetworkInfo>();
      if (network.GetAuthorityId() != clientId + 1) continue;
      if (!network.IsConfirmed()) continue;

      if (!cubes[i].GetComponent<Rigidbody>().IsSleeping()
        || network.GetLastActiveFrame() + ReturnToDefaultAuthorityFrames >= simulationFrame
      ) continue;

#if DEBUG_AUTHORITY
      Debug.Log( "client " + clientId + " returns cube " + i + " to default authority. increases authority sequence (" + network.GetAuthoritySequence() + "->" + (ushort) (network.GetAuthoritySequence() + 1 ) + ") and sets pending commit flag" );
#endif // #if DEBUG_AUTHORITY
      network.SetAuthorityId(0);
      network.IncreaseAuthoritySequence();

      if (IsClient())
        network.SetPendingCommit();
    }
    Profiler.EndSample();
  }

  public void UpdateCubePriority() {
    Profiler.BeginSample("UpdateCubeAuthority");
    Assert.IsTrue(IsActive());

    if (IsServer()) {
      for (int i = 1; i < MaxClients; ++i) {
        var data = GetServerData(i);
        UpdateCubePriority(data);
      }
    } else {
      var data = GetClientData();
      UpdateCubePriority(data);
    }
    Profiler.EndSample();
  }

  void SmoothCubes() {
    for (int i = 0; i < NumCubes; ++i)
      cubes[i].GetComponent<NetworkInfo>().Smooth();
  }

  void UpdateCubePriority(ConnectionData d) {
    Assert.IsTrue(snapshot != null);
    var frame = (long)GetSimulationFrame();

    for (int i = 0; i < NumCubes; ++i) {
      var network = cubes[i].GetComponent<NetworkInfo>();
      
      if (network.IsHeldByPlayer()) { //don't send state updates held cubes. they are synchronized differently.
        d.priorities[i].accumulator = -1;
        continue;
      }
      
      if (IsClient() && network.GetAuthorityId() == 0 && !network.IsPendingCommit()) { //only send white cubes from client -> server if they are pending commit after returning to default authority
        d.priorities[i].accumulator = -1;
        continue;
      }
      
      var priority = 1.0f; //base priority
      
      if (collisionFrames[i] + HighEnergyCollisionPriorityBoostNumFrames >= (ulong)frame) //higher priority for cubes that were recently in a high energy collision
        priority = 10.0f;
      
      if (network.GetLastFrame() + ThrownObjectPriorityBoostNumFrames >= frame) //*extremely* high priority for cubes that were just thrown by a player
        priority = 1000000.0f;

      d.priorities[i].accumulator += priority;
    }
  }

  public void GetMostImportantCubeStateUpdates(ConnectionData data, ref int numStateUpdates, ref int[] cubeIds, ref CubeState[] states) {
    Assert.IsTrue(numStateUpdates >= 0);
    Assert.IsTrue(numStateUpdates <= NumCubes);
    if (numStateUpdates == 0) return;

    var prioritySorted = new Priority[NumCubes];

    for (int i = 0; i < NumCubes; ++i)
      prioritySorted[i] = data.priorities[i];

    Array.Sort(prioritySorted, (x, y) => y.accumulator.CompareTo(x.accumulator));
    int maxStateUpdates = numStateUpdates;
    numStateUpdates = 0;

    for (int i = 0; i < NumCubes; ++i) {
      if (numStateUpdates == maxStateUpdates) break;
      if (prioritySorted[i].accumulator < 0.0f) continue; //IMPORTANT: Negative priority means don't send this cube!

      cubeIds[numStateUpdates] = prioritySorted[i].cubeId;
      states[numStateUpdates] = snapshot.cubeState[cubeIds[numStateUpdates]];
      ++numStateUpdates;
    }
  }

  void UpdatePendingCommit(NetworkInfo n, int authorityIndex, int fromClientIndex, int toClientIndex) {
    if (n.IsPendingCommit() && authorityIndex != toClientIndex + 1) {
#if DEBUG_AUTHORITY
      Debug.Log( "client " + toClientIndex + " sees update for cube " + n.GetCubeId() + " from client " + fromClientIndex + " with authority index (" + authorityIndex + ") and clears pending commit flag" );
#endif // #if DEBUG_AUTHORITY
      n.ClearPendingCommit();
    }
  }

  public void ApplyCubeStateUpdates(int numStateUpdates, ref int[] cubeIds, ref CubeState[] cubeState, int fromClientIndex, int toClientIndex, bool applySmoothing = true) {
    var origin = this.gameObject.transform.position;

    for (int i = 0; i < numStateUpdates; ++i) {
      if (AuthoritySystem.ShouldApplyCubeUpdate(this, cubeIds[i], cubeState[i].ownershipSequence, cubeState[i].authoritySequence, cubeState[i].authorityIndex, false, fromClientIndex, toClientIndex)) {
        var cube = cubes[cubeIds[i]];
        var network = cube.GetComponent<NetworkInfo>();
        var rigidBody = cube.GetComponent<Rigidbody>();

        UpdatePendingCommit(network, cubeState[i].authorityIndex, fromClientIndex, toClientIndex);
        Snapshot.ApplyCubeState(rigidBody, network, ref cubeState[i], ref origin, applySmoothing);
      }
    }
  }

  public void ApplyAvatarStateUpdates(int numAvatarStates, ref AvatarState[] avatarState, int fromClientIndex, int toClientIndex) {
    for (int i = 0; i < numAvatarStates; ++i) {
      if (toClientIndex == 0 && avatarState[i].client_index != fromClientIndex) continue;

      var avatar = GetAvatar(avatarState[i].client_index);

      if (avatarState[i].left_hand_holding_cube && AuthoritySystem.ShouldApplyCubeUpdate(this, avatarState[i].left_hand_cube_id, avatarState[i].left_hand_ownership_sequence, avatarState[i].left_hand_authority_sequence, avatarState[i].client_index + 1, true, fromClientIndex, toClientIndex)) {
        var cube = cubes[avatarState[i].left_hand_cube_id];
        var network = cube.GetComponent<NetworkInfo>();

        UpdatePendingCommit(network, avatarState[i].client_index + 1, fromClientIndex, toClientIndex);
        avatar.ApplyLeftHandUpdate(ref avatarState[i]);
      }

      if (avatarState[i].right_hand_holding_cube && AuthoritySystem.ShouldApplyCubeUpdate(this, avatarState[i].right_hand_cube_id, avatarState[i].right_hand_ownership_sequence, avatarState[i].right_hand_authority_sequence, avatarState[i].client_index + 1, true, fromClientIndex, toClientIndex)) {
        var cube = cubes[avatarState[i].right_hand_cube_id];
        var network = cube.GetComponent<NetworkInfo>();

        UpdatePendingCommit(network, avatarState[i].client_index + 1, fromClientIndex, toClientIndex);
        avatar.ApplyRightHandUpdate(ref avatarState[i]);
      }

      avatar.ApplyAvatarPose(ref avatarState[i]);
    }
  }

  public void ResetCubePriority(ConnectionData connectionData, int numCubes, int[] cubeIds) {
    for (int i = 0; i < numCubes; ++i)
      connectionData.priorities[cubeIds[i]].accumulator = 0.0f;
  }

  void AddStateToBuffer() {
    Profiler.BeginSample("AddStateToRingBuffer");
    int baseIndex = 0;
    var axis = new Vector3(1, 0, 0);

    for (int i = 0; i < NumCubes; i++) {
      var rigidBody = cubes[i].GetComponent<Rigidbody>();
      int index = baseIndex + (int)(simulationFrame % RingBufferSize);

      buffer[index].position = rigidBody.position;
      buffer[index].axis = rigidBody.rotation * axis;
      baseIndex += RingBufferSize;
    }
    Profiler.EndSample();
  }

  public void ResetBuffer(int cubeId) {
    int baseIndex = RingBufferSize * cubeId;

    for (int i = 0; i < RingBufferSize; ++i)
      buffer[baseIndex + i].position = new Vector3(1000000, 1000000, 1000000);

    int index = baseIndex + (int)(simulationFrame % RingBufferSize);
    buffer[index].position = Vector3.zero;
  }

  public void CheckForAtRestObjects() {
    Profiler.BeginSample("CheckForAtRestObjects");
    int baseIndex = 0;

    for (int i = 0; i < NumCubes; i++) {
      var rigidBody = cubes[i].GetComponent<Rigidbody>();
      var network = cubes[i].GetComponent<NetworkInfo>();

      if (rigidBody.IsSleeping()) {
        baseIndex += RingBufferSize;
        continue;
      }

      network.SetLastActiveFrame(simulationFrame);
      var currentPosition = buffer[baseIndex].position;
      var currentAxis = buffer[baseIndex].axis;
      var goToSleep = true;

      for (int j = 1; j < RingBufferSize; ++j) {
        int index = baseIndex + j;
        var positionDifference = buffer[index].position - currentPosition;

        if (positionDifference.sqrMagnitude > 0.01f * 0.01f) {
          goToSleep = false;
          break;
        }

        if (Vector3.Dot(buffer[index].axis, currentAxis) < 0.9999f) {
          goToSleep = false;
          break;
        }
      }

      if (goToSleep) rigidBody.Sleep();

      baseIndex += RingBufferSize;
    }
    Profiler.EndSample();
  }

  public void CaptureSnapshot(Snapshot s) {
    Profiler.BeginSample("CaptureSnapshot");
    var origin = gameObject.transform.position;

    for (int i = 0; i < NumCubes; i++) {
      var rigidBody = cubes[i].GetComponent<Rigidbody>();
      var network = cubes[i].GetComponent<NetworkInfo>();

      Snapshot.GetCubeState(rigidBody, network, ref s.cubeState[i], ref origin);
    }
    Profiler.EndSample();
  }

  public void ApplySnapshot(Snapshot s, bool skipAlreadyAtRest, bool skipHeldObjects) {
    Profiler.BeginSample("ApplySnapshot");
    var origin = gameObject.transform.position;

    for (int i = 0; i < NumCubes; i++) {
      var network = cubes[i].GetComponent<NetworkInfo>();
      if (skipHeldObjects && network.IsHeldByPlayer()) continue;

      var rigidBody = cubes[i].GetComponent<Rigidbody>();
      if (skipAlreadyAtRest && !s.cubeState[i].active && rigidBody.IsSleeping()) continue;

      Snapshot.ApplyCubeState(rigidBody, network, ref s.cubeState[i], ref origin);
    }
    Profiler.EndSample();
  }

  public Snapshot GetLastSnapshot() => snapshot;

  void InitAvatars() {
    for (int i = 0; i < MaxClients; ++i) {
      var avatar = GetAvatar(i);
      if (!avatar) continue;

      avatar.SetContext(this);
      avatar.SetClientIndex(i);
    }
  }

  void InitPriorities(ConnectionData data) {
    for (int i = 0; i < NumCubes; ++i)
      data.priorities[i].cubeId = i;
  }

  void InitCubePositions() {
#if DEBUG_AUTHORITY
    cubePositions[0] = new Vector3( -2, 10, 0 );
    cubePositions[1] = new Vector3( -1, 10, 0 );
    cubePositions[2] = new Vector3( -0, 10, 0 );
    cubePositions[3] = new Vector3( +1, 10, 0 );
    cubePositions[4] = new Vector3( +2, 10, 0 );
#else // #if DEBUG_AUTHORITY
    cubePositions[0] = new Vector3(3.299805f, 11.08789f, 0.2001948f);
    cubePositions[1] = new Vector3(-0.9501953f, 19.88574f, 0.7001948f);
    cubePositions[2] = new Vector3(-0.5996094f, 20.81055f, -1.008789f);
    cubePositions[3] = new Vector3(3.816406f, 20.78223f, 1.022461f);
    cubePositions[4] = new Vector3(3.922852f, 22.29199f, 1.323242f);
    cubePositions[5] = new Vector3(-0.04296875f, 11.92383f, -0.8212891f);
    cubePositions[6] = new Vector3(0.2001953f, 18.6875f, -0.2001948f);
    cubePositions[7] = new Vector3(-2.599609f, 20.08789f, -0.0996089f);
    cubePositions[8] = new Vector3(2.299805f, 20.48535f, 0.2001948f);
    cubePositions[9] = new Vector3(-3.482422f, 21.58398f, -0.9824219f);
    cubePositions[10] = new Vector3(-1.5f, 15.08496f, -0.2001948f);
    cubePositions[11] = new Vector3(1.099609f, 18.8877f, -0.4003911f);
    cubePositions[12] = new Vector3(-1.299805f, 12.48535f, -0.5996089f);
    cubePositions[13] = new Vector3(2.200195f, 21.1875f, 1.900391f);
    cubePositions[14] = new Vector3(2.900391f, 22.58789f, -0.5996089f);
    cubePositions[15] = new Vector3(-0.5996094f, 21.89941f, -0.7998052f);
    cubePositions[16] = new Vector3(1.200195f, 19.6875f, -1.200195f);
    cubePositions[17] = new Vector3(-2.900391f, 13.6875f, -0.5f);
    cubePositions[18] = new Vector3(-1.773438f, 16.29688f, -0.6669922f);
    cubePositions[19] = new Vector3(-1.200195f, 17.59766f, -0.5f);
    cubePositions[20] = new Vector3(0f, 13.98828f, 0.5996089f);
    cubePositions[21] = new Vector3(-1.299805f, 18.6875f, 0.4003911f);
    cubePositions[22] = new Vector3(-4f, 12.1875f, -1.799805f);
    cubePositions[23] = new Vector3(3.5f, 15.10449f, 0.109375f);
    cubePositions[24] = new Vector3(-0.02050781f, 16.66699f, 0.202148f);
    cubePositions[25] = new Vector3(1.099609f, 17.19043f, -1.617188f);
    cubePositions[26] = new Vector3(1.299805f, 21.58789f, 0.0996089f);
    cubePositions[27] = new Vector3(1.799805f, 12.1875f, 0.4003911f);
    cubePositions[28] = new Vector3(3.828125f, 20.28418f, 1.139648f);
    cubePositions[29] = new Vector3(1f, 14.98828f, -2f);
    cubePositions[30] = new Vector3(3.700195f, 19.3877f, 0f);
    cubePositions[31] = new Vector3(3.400391f, 12.78809f, 0.5f);
    cubePositions[32] = new Vector3(2.599609f, 17.1875f, -1.5f);
    cubePositions[33] = new Vector3(-2.700195f, 20.3877f, 1.599609f);
    cubePositions[34] = new Vector3(1.900391f, 13.78809f, 0.5996089f);
    cubePositions[35] = new Vector3(-0.9003906f, 15.1875f, -2f);
    cubePositions[36] = new Vector3(-1.400391f, 18.08789f, 0.5f);
    cubePositions[37] = new Vector3(0.2558594f, 20.7168f, 0.9023442f);
    cubePositions[38] = new Vector3(-0.09960938f, 12.8877f, 0.4003911f);
    cubePositions[39] = new Vector3(-3.900391f, 15.98828f, -1.099609f);
    cubePositions[40] = new Vector3(1.823242f, 13.60254f, -0.2412109f);
    cubePositions[41] = new Vector3(-2.900391f, 15.6875f, 0f);
    cubePositions[42] = new Vector3(-0.7998047f, 18.1875f, -0.5996089f);
    cubePositions[43] = new Vector3(-4f, 12.8877f, -2.5f);
    cubePositions[44] = new Vector3(2.356445f, 24.45703f, 1.677734f);
    cubePositions[45] = new Vector3(1.999023f, 16.95703f, 0.1943359f);
    cubePositions[46] = new Vector3(3.246094f, 11.16699f, -0.7314448f);
    cubePositions[47] = new Vector3(2.319336f, 21.33887f, 1.157227f);
    cubePositions[48] = new Vector3(0.2998047f, 20.28809f, 1.700195f);
    cubePositions[49] = new Vector3(-1.299805f, 16.78906f, 0.2998052f);
    cubePositions[50] = new Vector3(1.900391f, 13.5957f, -1.099609f);
    cubePositions[51] = new Vector3(2.700195f, 17.6875f, -1.400391f);
    cubePositions[52] = new Vector3(3.396484f, 12.81934f, -0.3037109f);
    cubePositions[53] = new Vector3(0f, 13.28809f, -1.200195f);
    cubePositions[54] = new Vector3(0.2001953f, 19.78809f, 1.599609f);
    cubePositions[55] = new Vector3(3.799805f, 22.98828f, 2.299805f);
    cubePositions[56] = new Vector3(0.07128906f, 18.74121f, 0.6630859f);
    cubePositions[57] = new Vector3(-1f, 14.3877f, -1.299805f);
    cubePositions[58] = new Vector3(-0.01367188f, 13.70801f, -0.390625f);
    cubePositions[59] = new Vector3(2.202148f, 20.27637f, 0.7470698f);
    cubePositions[60] = new Vector3(0.078125f, 18.02441f, 0.7080078f);
    cubePositions[61] = new Vector3(0.2998047f, 21.48828f, 1.900391f);
    cubePositions[62] = new Vector3(-2.799805f, 16.78809f, 1f);
    cubePositions[63] = new Vector3(-1.529297f, 19.92676f, -0.07519484f);
#endif // #if DEBUG_AUTHORITY
  }

  public void WriteCubePositionsToFile(String filename) {
    var origin = gameObject.transform.position;

    using (var file = new StreamWriter(filename)) {
      for (int i = 0; i < NumCubes; i++) {
        var rigidBody = cubes[i].GetComponent<Rigidbody>();
        var position = rigidBody.position - origin;

        file.WriteLine("cubePositions[" + i + "] = new Vector3( " + position.x + "f, " + position.y + "f, " + position.z + "f );");
      }
    }
  }

  public void TestSmoothing() {
    for (int i = 0; i < NumCubes; i++) {
      var networkInfo = cubes[i].GetComponent<NetworkInfo>();
      var rigidBody = cubes[i].GetComponent<Rigidbody>();

      networkInfo.MoveWithSmoothing(cubes[i].transform.position + new Vector3(0, 10, 0), Quaternion.identity);
      rigidBody.velocity = Vector3.zero;
      rigidBody.angularVelocity = Vector3.zero;
    }
  }
}