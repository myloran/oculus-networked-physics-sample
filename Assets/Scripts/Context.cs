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
using System.Collections.Generic;
using System.Linq;
using Network;
using System.IO;
using static UnityEngine.Assertions.Assert;
using static UnityEngine.Profiling.Profiler;
using static UnityEngine.Quaternion;
using static UnityEngine.Vector3;
using static Constants;
using static Snapshot;
using static AuthoritySystem;

/// <summary>
/// Inits avatars and cubes. Spreads cube authority. Returns cubes to neutral priority if they come to rest. Smoothes cubes.
/// Captures and applies snapshot.
/// </summary>
public class Context : MonoBehaviour {
  public struct CubePriority {
    public int cubeId;
    public float value;
  };

  public struct CubeAck {
    public CubeState state;

    public ushort 
      id,
      resetId;

    public bool isAcked;
  };

  struct RingBuffer {
    public Vector3 
      position,
      axis;
  };

  public class NetworkData {
    public PacketAcking acking = new PacketAcking();

    public DeltaBuffer 
      sendBuffer = new DeltaBuffer(DeltaBufferSize),
      receiveBuffer = new DeltaBuffer(DeltaBufferSize);

    public JitterBuffer jitterBuffer = new JitterBuffer();
    public CubePriority[] priorities = new CubePriority[MaxCubes];
    public CubeAck[] acks = new CubeAck[MaxCubes];
    public bool isFirstPacket = true;
    public long frame = -1;

    public NetworkData() {
      Reset();
    }

    public void Reset() {
      BeginSample("ConnectionData.Reset");
      acking.Reset();
      sendBuffer.Reset();
      receiveBuffer.Reset();

      for (int i = 0; i < priorities.Length; ++i) {
        priorities[i].cubeId = i;
        priorities[i].value = 0.0f;
      }

      for (int i = 0; i < acks.Length; ++i) {
        acks[i].isAcked = false;
        acks[i].id = 0;
        acks[i].resetId = 0;
      }

      isFirstPacket = true;
      frame = -1;
      jitterBuffer.Reset();
      EndSample();
    }
  };

  public GameObject[]
    remoteAvatar = new GameObject[MaxClients],
    remoteLinePrefabs = new GameObject[MaxClients];

  public Material[] authorityMaterials = new Material[MaxAuthority];
  public GameObject cubePrefab;
  NetworkData[] server;
  public GameObject[] cubes = new GameObject[MaxCubes];
  HashSet<int> visited = new HashSet<int>();
  NetworkData client;
  Interactions Interactions = new Interactions();
  Snapshot snapshot = new Snapshot();
  Vector3[] cubePositions = new Vector3[MaxCubes];
  RingBuffer[] buffer = new RingBuffer[MaxCubes * RingBufferSize];
  ulong[] collisionFrames = new ulong[MaxCubes];

  public ulong
    renderFrame = 0,
    simulationFrame = 0;

  public int
    clientId,
    authorityId,
    layer;

  bool isActive = true;
  public ushort resetId = 0;

  void Awake() {
    IsTrue(cubePrefab);
    layer = gameObject.layer;
    InitAvatars();
    InitCubePositions();
    CreateCubes();
  }

  public void Update() {
    if (!isActive) return;

    SpreadAuthority();
    BeginSample("UpdateAuthorityMaterials");
    UpdateAuthorityMaterials();
    EndSample();
    renderFrame++;
  }

  public void LateUpdate() => SmoothCubes();

  public void FixedUpdate() {
    if (!isActive) return;

    SpreadAuthority();
    CaptureSnapshot(snapshot);
    ApplySnapshot(snapshot, true, true);
    AddStateToBuffer();
    UpdateAvatars(); //is this needed?
    UpdateCubesAuthority();
    simulationFrame++;
  }

  public void Reset() {
    BeginSample("Reset");
    IsTrue(isActive);
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
    EndSample();
  }

  public NetworkData GetClientData() {
    IsTrue(IsClient());

    return client;
  }

  public NetworkData GetServerData(int id) {
    IsTrue(IsServer());
    IsTrue(id >= 1);
    IsTrue(id <= MaxClients);

    return server[id - 1];
  }

  public void Init(int id) {
    clientId = id;
    authorityId = id + 1;
    IsTrue(clientId >= 0 && clientId < MaxClients);
    IsTrue(authorityId >= 0 && authorityId < MaxAuthority);

    if (id == 0) {
      client = null; //initialize as server
      server = new NetworkData[MaxClients - 1];

      for (int i = 0; i < server.Length; ++i) {
        server[i] = new NetworkData();
        InitPriorities(server[i]);
      }
    } else {
      client = new NetworkData(); //initialize as client
      server = null;
      InitPriorities(client);
    }
  }

  public void Shutdown() {
    clientId = 0;
    authorityId = 0;
    client = null;
    server = null;
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

  public bool IsServer() => isActive && clientId == 0;
  public bool IsClient() => isActive && clientId != 0;
  public int GetGripLayer() => layer + 1;
  public int GetTouchingLayer() => layer + 2;

  public RemoteAvatar GetAvatar(int id) {
    IsTrue(id >= 0);
    IsTrue(id < MaxClients);

    return remoteAvatar[id]?.GetComponent<RemoteAvatar>();
  }

  void ShowObj(GameObject obj, bool show) {
    foreach (var renderer in obj.GetComponentsInChildren<Renderer>())
      renderer.enabled = show;
  }

  public void ShowAvatar(int id) => ShowObj(GetAvatar(id).gameObject, true);
  public void HideAvatar(int id) => ShowObj(GetAvatar(id).gameObject, false);

  public void ResetAuthority(int clientId) {
    for (int i = 0; i < MaxCubes; ++i) {
      var cube = cubes[i].GetComponent<NetworkCube>();
      if (cube.authorityId != clientId + 1) continue;

      Debug.Log("Returning cube " + i + " to default authority");
      cube.Release();
      cube.authorityId = 0;
      cube.authorityPacketId = 0;
      cube.ownershipId++;
      var body = cubes[i].GetComponent<Rigidbody>();

      if (body.IsSleeping())
        body.WakeUp();

      ResetBuffer(i);
    }
  }

  public GameObject GetAvatarHead(int id) => GetAvatar(id)?.GetHead();

  public bool GetAckedCube(NetworkData d, int cubeId, ref ushort packetId, ushort resetId, ref CubeState state) {
    if (!d.acks[cubeId].isAcked) return false;
    if (d.acks[cubeId].resetId != resetId) return false;

    packetId = d.acks[cubeId].id;
    state = d.acks[cubeId].state;

    return true;
  }

  public bool UpdateCubeAck(NetworkData d, int cubeId, ushort packetId, ushort resetId, ref CubeState state) {
    if (d.acks[cubeId].isAcked
      && (Util.IdGreaterThan(d.acks[cubeId].resetId, resetId) || Util.IdGreaterThan(d.acks[cubeId].id, packetId))
    ) return false;

    d.acks[cubeId].isAcked = true;
    d.acks[cubeId].id = packetId;
    d.acks[cubeId].resetId = resetId;
    d.acks[cubeId].state = state;

    return true;
  }

  void UpdateAuthorityMaterials() {
    for (int i = 0; i < MaxCubes; i++) {
      var cube = cubes[i].GetComponent<NetworkCube>();
      var renderer = cube.smoothed.GetComponent<Renderer>();

      renderer.material.Lerp(
        renderer.material,
        authorityMaterials[cube.authorityId],
        cube.authorityId != 0 ? 0.3f : 0.04f);
    }
  }

  void CreateCubes() {
    BeginSample("CreateCubes");
    for (int i = 0; i < MaxCubes; i++) {
      if (!cubes[i]) {
        cubes[i] = Instantiate(cubePrefab, gameObject.transform.position + cubePositions[i], identity); //cube initial create
        cubes[i].layer = gameObject.layer;
        var cube = cubes[i].GetComponent<NetworkCube>();
        var body = cubes[i].GetComponent<Rigidbody>();

        body.maxDepenetrationVelocity = PushOutVelocity; //this is *extremely* important to reduce jitter in the remote view of large stacks of rigid bodies
        cube.touching.layer = GetTouchingLayer();
        cube.Init(this, i);
      } else {
        var body = cubes[i].GetComponent<Rigidbody>(); //cube already exists: force it back to initial state

        if (body.IsSleeping())
          body.WakeUp();

        body.position = cubePositions[i] + gameObject.transform.position;
        body.rotation = identity;
        body.velocity = zero;
        body.angularVelocity = zero;
        ResetBuffer(i);

        var cube = cubes[i].GetComponent<NetworkCube>();
        cube.Release();
        cube.authorityId = 0;
        cube.authorityPacketId = 0;
        cube.ownershipId = 0;

        var renderer = cube.smoothed.GetComponent<Renderer>();
        renderer.material = authorityMaterials[0];
        cube.positionLag = zero;
        cube.rotationLag = identity;
        cubes[i].transform.parent = null;
      }
    }
    EndSample();
  }

  void ShowContext(bool show) {
    ShowObj(gameObject, show);

    for (int i = 0; i < MaxCubes; i++) {
      if (!cubes[i]) continue;

      var cube = cubes[i].GetComponent<NetworkCube>();
      cube.smoothed.GetComponent<Renderer>().enabled = show;
    }
  }

  public void FreezeCubes(bool freeze) {
    for (int i = 0; i < MaxCubes; i++) {
      if (!cubes[i]) continue;

      cubes[i].GetComponent<Rigidbody>().isKinematic = freeze;
    }
  }

  public void Collide(int cubeId1, int cubeId2, Collision collision) {
    if (collision.relativeVelocity.sqrMagnitude <= HighEnergyCollisionThreshold * HighEnergyCollisionThreshold) return;

    collisionFrames[cubeId1] = simulationFrame;

    if (cubeId2 != CollisionWithFloor)
      collisionFrames[cubeId2] = simulationFrame;
  }

  public void StartTouching(int cubeId1, int cubeId2)
    => Interactions.Add((ushort)cubeId1, (ushort)cubeId2);

  public void FinishTouching(int cubeId1, int cubeId2)
    => Interactions.Remove((ushort)cubeId1, (ushort)cubeId2);

  public void FindSupports(GameObject obj, ref HashSet<GameObject> supports) {
    if (supports.Contains(obj)) return;

    supports.Add(obj);
    int id = obj.GetComponent<NetworkCube>().cubeId;

    for (int i = 0; i < MaxCubes; ++i) {
      if (Interactions.Get(id).interactions[i] == 0) continue;
      if (cubes[i].layer != layer) continue;
      if (cubes[i].transform.position.y < obj.transform.position.y + SupportHeightThreshold) continue;

      FindSupports(cubes[i], ref supports);
    }
  }

  public List<GameObject> FindSupports(GameObject obj) {
    // Support objects are used to determine the set of objects that should be woken up when you grab a cube.
    // Without this, objects resting on the cube you grab stay floating in the air. This function tries to only
    // wake up objects that are above (resting on) the game object that is being recursively walked. The idea being
    // if you grab an object in the middle of a stack, it wakes up any objects above it, but not below or to the side.
    var supports = new HashSet<GameObject>();
    FindSupports(obj, ref supports);

    return supports.ToList();
  }

  public void TakeAuthority(NetworkCube cube) {
    IsTrue(cube.authorityId == 0);
#if DEBUG_AUTHORITY
    Debug.Log( "client " + clientIndex + " took authority over cube " + n.GetCubeId() );
#endif // #if DEBUG_AUTHORITY
    cube.authorityId = authorityId;
    cube.authorityPacketId++;
    cube.isConfirmed = IsServer();
  }

  void SpreadAutority(int cubeId) {
    if (visited.Contains(cubeId)) {
      IsTrue(cubes[cubeId].GetComponent<NetworkCube>().authorityId == authorityId);
      return;
    }

    visited.Add(cubeId);
    var interactions = Interactions.Get(cubeId).interactions;

    for (int i = 0; i < MaxCubes; ++i) {
      if (interactions[i] == 0) continue;

      var cube = cubes[i].GetComponent<NetworkCube>();
      if (cube.authorityId != 0) continue;

      TakeAuthority(cube);
      SpreadAutority(i);
    }
  }

  void SpreadAuthority() {
    BeginSample("ProcessInteractions");
    visited.Clear();

    for (int i = 0; i < MaxCubes; ++i) {
      var cube = cubes[i].GetComponent<NetworkCube>();
      if (cube.authorityId != authorityId) continue;
      if (cube.HasHolder()) continue;
      if (cubes[i].GetComponent<Rigidbody>().IsSleeping()) continue;

      SpreadAutority((ushort)i);
    }
    EndSample();
  }

  void UpdateAvatars() {
    BeginSample("UpdateRemoteAvatars");

    for (int i = 0; i < MaxClients; ++i)
      GetAvatar(i)?.Update();

    EndSample();
  }

  void UpdateCubesAuthority() {
    BeginSample("UpdateCubeAuthority");
    /*
     * After objects have been at rest for some period of time they return to default authority (white).
     * This logic runs on the client that has authority over the object. To avoid race conditions where the 
     * first client to activate an object and put it to rest wins in case of a conflict, the client delays
     * returning an object to default authority until after it has received confirmation from the server that
     * it has authority over that object.
     */
    for (int i = 0; i < MaxCubes; ++i) {
      var cube = cubes[i].GetComponent<NetworkCube>();
      if (cube.authorityId != clientId + 1) continue;
      if (!cube.isConfirmed) continue;

      if (!cubes[i].GetComponent<Rigidbody>().IsSleeping()
        || cube.activeFrame + ReturnToDefaultAuthorityFrames >= simulationFrame
      ) continue;
#if DEBUG_AUTHORITY
      Debug.Log( "client " + clientId + " returns cube " + i + " to default authority. increases authority sequence (" + network.GetAuthoritySequence() + "->" + (ushort) (network.GetAuthoritySequence() + 1 ) + ") and sets pending commit flag" );
#endif // #if DEBUG_AUTHORITY
      cube.authorityId = 0;
      cube.authorityPacketId++;

      if (IsClient())
        cube.isPendingCommit = true;
    }
    EndSample();
  }

  public void UpdateCubePriorities() { //separate client and server logic
    BeginSample("UpdateCubeAuthority");
    IsTrue(isActive);

    if (IsServer()) {
      for (int i = 1; i < MaxClients; ++i) {
        var data = GetServerData(i);
        UpdateCubePriorities(data);
      }
    } else {
      var data = GetClientData();
      UpdateCubePriorities(data);
    }
    EndSample();
  }

  void SmoothCubes() {
    for (int i = 0; i < MaxCubes; ++i)
      cubes[i].GetComponent<NetworkCube>().Smooth();
  }

  void UpdateCubePriorities(NetworkData data) {
    IsTrue(snapshot != null);

    for (int i = 0; i < MaxCubes; ++i) {
      var cube = cubes[i].GetComponent<NetworkCube>();
      if (cube.HasHolder()) { //don't send state updates held cubes. they are synchronized differently.
        data.priorities[i].value = DontSend;
        continue;
      }
      if (IsClient() && cube.authorityId == 0 && !cube.isPendingCommit) { //only send white cubes from client -> server if they are pending commit after returning to default authority
        data.priorities[i].value = DontSend;
        continue;
      }

      var priority = 1.0f; //base priority

      if (collisionFrames[i] + CollisionPriority >= simulationFrame)
        priority = 10.0f; //higher priority for cubes that were recently in a high energy collision

      if (cube.heldFrame + ThrownObjectPriority >= (long)simulationFrame)
        priority = 1000000.0f; //*extremely* high priority for cubes that were just thrown by a player

      data.priorities[i].value += priority;
    }
  }

  public void GetCubeUpdates(NetworkData data, ref int count, ref int[] ids, ref CubeState[] cubes) {
    IsTrue(count >= 0);
    IsTrue(count <= MaxCubes);
    if (count == 0) return;

    var priorities = new CubePriority[MaxCubes];

    for (int i = 0; i < MaxCubes; ++i)
      priorities[i] = data.priorities[i];

    Array.Sort(priorities, (x, y) => y.value.CompareTo(x.value));
    int max = count;
    count = 0;

    for (int i = 0; i < MaxCubes; ++i) {
      if (count == max) break;
      if (priorities[i].value <= DontSend) continue;

      int id = priorities[i].cubeId;
      ids[count] = id;
      cubes[count] = snapshot.states[id];
      ++count;
    }
  }

  void UpdatePendingCommit(NetworkCube cube, int authorityId, int fromClientId, int toClientId) {
    if (cube.isPendingCommit && authorityId != toClientId + 1) {
#if DEBUG_AUTHORITY
      Debug.Log( "client " + toClientId + " sees update for cube " + n.GetCubeId() + " from client " + fromClientId + " with authority index (" + authorityId + ") and clears pending commit flag" );
#endif // #if DEBUG_AUTHORITY
      cube.isPendingCommit = false;
    }
  }

  public void ApplyCubeUpdates(int count, ref int[] cubeIds, ref CubeState[] states, int fromClientId, int toClientId, bool isSmooth = true) {
    var origin = gameObject.transform.position;

    for (int i = 0; i < count; ++i) {
      if (!ShouldApplyUpdate(this, cubeIds[i], states[i].ownershipId, states[i].authorityPacketId, states[i].authorityId, false, fromClientId, toClientId))
        continue;

      var obj = cubes[cubeIds[i]];
      var cube = obj.GetComponent<NetworkCube>();
      var body = obj.GetComponent<Rigidbody>();

      UpdatePendingCommit(cube, states[i].authorityId, fromClientId, toClientId);
      ApplyCubeState(body, cube, ref states[i], ref origin, isSmooth);
    }
  }

  public void ApplyAvatarUpdates(int count, ref AvatarState[] s, int fromClientId, int toClientId) {
    for (int i = 0; i < count; ++i) {
      if (toClientId == 0 && s[i].clientId != fromClientId) continue;

      var avatar = GetAvatar(s[i].clientId);

      if (s[i].isLeftHandHoldingCube && ShouldApplyUpdate(this, s[i].leftHandCubeId, s[i].leftHandOwnershipId, s[i].leftHandAuthorityId, s[i].clientId + 1, true, fromClientId, toClientId)
      ) {
        var obj = cubes[s[i].leftHandCubeId];
        var cube = obj.GetComponent<NetworkCube>();

        UpdatePendingCommit(cube, s[i].clientId + 1, fromClientId, toClientId);
        avatar.ApplyLeftHandUpdate(ref s[i]);
      }

      if (s[i].isRightHandHoldingCube && ShouldApplyUpdate(this, s[i].rightHandCubeId, s[i].rightHandOwnershipId, s[i].rightHandAuthorityId, s[i].clientId + 1, true, fromClientId, toClientId)
      ) {
        var obj = cubes[s[i].rightHandCubeId];
        var cube = obj.GetComponent<NetworkCube>();

        UpdatePendingCommit(cube, s[i].clientId + 1, fromClientId, toClientId);
        avatar.ApplyRightHandUpdate(ref s[i]);
      }

      avatar.ApplyAvatarPose(ref s[i]);
    }
  }

  public void ResetCubePriority(NetworkData data, int count, int[] cubeIds) {
    for (int i = 0; i < count; ++i)
      data.priorities[cubeIds[i]].value = 0.0f;
  }

  void AddStateToBuffer() {
    BeginSample("AddStateToRingBuffer");
    int baseId = 0;
    var axis = new Vector3(1, 0, 0);

    for (int i = 0; i < MaxCubes; i++) {
      var body = cubes[i].GetComponent<Rigidbody>();
      int id = baseId + (int)(simulationFrame % RingBufferSize);

      buffer[id].position = body.position;
      buffer[id].axis = body.rotation * axis;
      baseId += RingBufferSize;
    }
    EndSample();
  }

  public void ResetBuffer(int cubeId) {
    int baseId = RingBufferSize * cubeId;

    for (int i = 0; i < RingBufferSize; ++i)
      buffer[baseId + i].position = new Vector3(1000000, 1000000, 1000000);

    int id = baseId + (int)(simulationFrame % RingBufferSize); //what is this doing?
    buffer[id].position = zero;
  }

  public void UpdateSleep() {
    BeginSample("CheckForAtRestObjects");
    int baseId = 0;

    for (int i = 0; i < MaxCubes; i++) {
      var body = cubes[i].GetComponent<Rigidbody>();
      var cube = cubes[i].GetComponent<NetworkCube>();

      if (body.IsSleeping()) {
        baseId += RingBufferSize;
        continue;
      }
      cube.activeFrame = simulationFrame;
      var needSleep = true;

      for (int j = 1; j < RingBufferSize; ++j) {
        var diff = buffer[baseId + j].position - buffer[baseId].position;

        if (diff.sqrMagnitude > 0.01f * 0.01f) {
          needSleep = false;
          break;
        }

        if (Dot(buffer[baseId + j].axis, buffer[baseId].axis) < 0.9999f) {
          needSleep = false;
          break;
        }
      }

      if (needSleep) body.Sleep();

      baseId += RingBufferSize;
    }
    EndSample();
  }

  public void CaptureSnapshot(Snapshot snapshot) {
    BeginSample("CaptureSnapshot");
    var origin = gameObject.transform.position;

    for (int i = 0; i < MaxCubes; i++) {
      var body = cubes[i].GetComponent<Rigidbody>();
      var cube = cubes[i].GetComponent<NetworkCube>();

      GetState(body, cube, ref snapshot.states[i], ref origin);
    }
    EndSample();
  }

  public void ApplySnapshot(Snapshot snapshot, bool skipSleepers, bool skipHeldObjects) {
    BeginSample("ApplySnapshot");
    var origin = gameObject.transform.position;

    for (int i = 0; i < MaxCubes; i++) {
      var cube = cubes[i].GetComponent<NetworkCube>();
      if (skipHeldObjects && cube.HasHolder()) continue;

      var body = cubes[i].GetComponent<Rigidbody>();
      if (skipSleepers && !snapshot.states[i].isActive && body.IsSleeping()) continue;

      ApplyCubeState(body, cube, ref snapshot.states[i], ref origin);
    }
    EndSample();
  }

  public Snapshot GetLastSnapshot() => snapshot;

  void InitAvatars() {
    for (int i = 0; i < MaxClients; ++i) {
      var avatar = GetAvatar(i);
      if (!avatar) continue;

      avatar.SetContext(this);
      avatar.SetClientId(i);
    }
  }

  void InitPriorities(NetworkData data) {
    for (int i = 0; i < MaxCubes; ++i)
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

  public void WriteCubePositionsToFile(string filename) {
    var origin = gameObject.transform.position;

    using (var file = new StreamWriter(filename)) {
      for (int i = 0; i < MaxCubes; i++) {
        var body = cubes[i].GetComponent<Rigidbody>();
        var position = body.position - origin;

        file.WriteLine("cubePositions[" + i + "] = new Vector3( " + position.x + "f, " + position.y + "f, " + position.z + "f );");
      }
    }
  }

  public void TestSmoothing() {
    for (int i = 0; i < MaxCubes; i++) {
      var cube = cubes[i].GetComponent<NetworkCube>();
      var body = cubes[i].GetComponent<Rigidbody>();

      cube.SmoothMove(cubes[i].transform.position + new Vector3(0, 10, 0), identity);
      body.velocity = zero;
      body.angularVelocity = zero;
    }
  }
}