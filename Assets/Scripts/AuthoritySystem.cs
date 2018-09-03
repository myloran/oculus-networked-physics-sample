/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using static Network.Util;

public struct AuthoritySystem {
  /*
   *  This function determines when we should apply state updates to cubes.
   *  It is designed to allow clients to pre-emptively take authority over cubes when
   *  they grab and interact with them indirectly (eg. throwing cubes at other cubes).
   *  In short, ownership sequence increases each time a player grabs a cube, and authority
   *  sequence increases each time a cube is touched by a cube under authority of that player.
   *  When a client sees a cube under its authority has come to rest, it returns that cube to
   *  default authority and commits its result back to the server. The logic below implements
   *  this direction of flow, as well as resolving conflicts when two clients think they both
   *  own the same cube, or have interacted with the same cube. The first player to interact, 
   *  from the point of view of the server (client 0), wins.
   */
  public static bool ShouldApplyUpdate(Context context, int cubeId, ushort ownershipSequence, ushort authoritySequence, int authorityId, bool fromAvatar, int fromClientId, int toClientId) {
    var cube = context.GetCube(cubeId);
    var network = cube.GetComponent<CubeNetworkInfo>();
    var localOwnershipSequence = network.GetOwnershipSequence();
    var localAuthoritySequence = network.GetAuthoritySequence();
    int localAuthorityId = network.GetAuthorityId();
    // *** OWNERSHIP SEQUENCE ***    
    if (SequenceGreaterThan(ownershipSequence, localOwnershipSequence)) { //Must accept if ownership sequence is newer
#if DEBUG_AUTHORITY
            Debug.Log( "client " + toClientIndex + " sees new ownership sequence (" + localOwnershipSequence + "->" + ownershipSequence + ") for cube " + cubeId + " and accepts update" );
#endif // #if DEBUG_AUTHORITY
      return true;
    }
    if (SequenceLessThan(ownershipSequence, localOwnershipSequence)) return false; //Must reject if ownership sequence is older
    //*** AUTHORITY SEQUENCE ***    
    if (SequenceGreaterThan(authoritySequence, localAuthoritySequence)) { //accept if the authority sequence is newer
#if DEBUG_AUTHORITY
            Debug.Log( "client " + toClientIndex + " sees new authority sequence (" + localAuthoritySequence + "->" + authoritySequence + ") for cube " + cubeId + " and accepts update" );
#endif // #if DEBUG_AUTHORITY
      return true;
    }
    if (SequenceLessThan(authoritySequence, localAuthoritySequence)) return false; //reject if the authority sequence is older
    if (fromClientId == 0) { //Both sequence numbers are the same. Resolve authority conflicts!
      // =============================
      //       server -> client
      // =============================      
      if (authorityId == toClientId + 1) { //ignore if the server says the cube is under authority of this client. the server is just confirming we have authority
        if (!network.isConfirmed) {
#if DEBUG_AUTHORITY
                    Debug.Log( "client " + fromClientIndex + " confirms client " + toClientIndex + " has authority over cube " + cubeId + " (" + ownershipSequence + "," + authoritySequence + ")" );
#endif // #if DEBUG_AUTHORITY
          network.isConfirmed = true;
        }
        return false;
      }
      if (authorityId != 0 && authorityId != toClientId + 1) { //accept if the server says the cube is under authority of another client
        if (localAuthorityId == toClientId + 1) {
#if DEBUG_AUTHORITY
                    Debug.Log( "client " + toClientIndex + " lost authority over cube " + cubeId + " to client " + ( authorityIndex - 1 ) + " (" + ownershipSequence + "," + authoritySequence + ")" );
#endif // #if DEBUG_AUTHORITY
        }
        return true;
      }
      if (authorityId == 0 && localAuthorityId == toClientId + 1) return false; //ignore if the server says the cube is default authority, but the client has already taken authority over the cube
      if (authorityId == 0 && localAuthorityId == 0) return true; //accept if the server says the cube is default authority, and on the client it is also default authority
    } else {
      // =============================
      //       client -> server
      // =============================
      if (authorityId != fromClientId + 1) return false; //reject if the cube is not under authority of the client
      if (localAuthorityId == fromClientId + 1) return true; //accept if the cube is under authority of this client
    }    
    return false; //otherwise, reject.
  }
}