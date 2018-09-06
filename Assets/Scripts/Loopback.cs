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

public class Loopback: Common
{
    public Context hostContext;
    public Context guestContext;

    Context currentContext;

    Context GetContext( int clientIndex )
    {
        switch ( clientIndex )
        {
            case 0: return hostContext;
            case 1: return guestContext;
            default: return null;
        }
    }

    void InitializeContexts()
    {
        for ( int clientIndex = 0; clientIndex < Constants.MaxClients; ++clientIndex )
        {
            Context context = GetContext( clientIndex );
            if ( context != null )
                context.Init( clientIndex );
        }
    }

    new void Awake()
    {
        InitializeContexts();
    }

    new void Start()
    {
        base.Start();

        SwitchToHostContext();

        simulator.SetLatency( 50.0f );               // 100ms round trip
        simulator.SetJitter( 50.0f );                // add a bunch of jitter!

#if DEBUG_DELTA_COMPRESSION
        networkSimulator.SetPacketLoss( 25.0f );
#endif // #if DEBUG_DELTA_COMPRESSION
    }

    void SwitchToHostContext()
    {
        if ( currentContext == hostContext )
            return;

        Profiler.BeginSample( "SwitchToHostContext" );

        hostContext.HideAvatar( 0 );
        hostContext.ShowAvatar( 1 );

        guestContext.ShowAvatar( 1 );
        guestContext.ShowAvatar( 1 );

        localAvatar.GetComponent<Hands>().SetContext( hostContext.GetComponent<Context>() );
        localAvatar.transform.position = hostContext.GetAvatar( 0 ).gameObject.transform.position;
        localAvatar.transform.rotation = hostContext.GetAvatar( 0 ).gameObject.transform.rotation;

        currentContext = hostContext;

        Profiler.EndSample();
    }

    void SwitchToGuestContext()
    {
        if ( currentContext == guestContext )
            return;

        Profiler.BeginSample( "SwitchToGuestContext" );

        hostContext.ShowAvatar( 0 );
        hostContext.ShowAvatar( 1 );

        guestContext.ShowAvatar( 0 );
        guestContext.HideAvatar( 1 );

        localAvatar.GetComponent<Hands>().SetContext( guestContext.GetComponent<Context>() );
        localAvatar.transform.position = guestContext.GetAvatar( 1 ).gameObject.transform.position;
        localAvatar.transform.rotation = guestContext.GetAvatar( 1 ).gameObject.transform.rotation;

        currentContext = guestContext;

        Profiler.EndSample();
    }

    new void Update()
    {
        base.Update();

        if ( Input.GetKeyDown( "return" ) )
        {
            hostContext.TestSmoothing();
        }

        // apply host avatar state at render time with interpolation

        for ( int i = 1; i < Constants.MaxClients; ++i )
        {
            Context context = hostContext;

            Context.NetworkData connectionData = context.GetServerData( i );

            int fromClientIndex = i;
            int toClientIndex = 0;

            int numInterpolatedAvatarStates;
            ushort avatarResetSequence;
            if ( connectionData.jitterBuffer.GetInterpolatedAvatars( ref interpolatedAvatars, out numInterpolatedAvatarStates, out avatarResetSequence ) )
            {
                if ( avatarResetSequence == context.resetId)
                {
                    context.ApplyAvatarUpdates( numInterpolatedAvatarStates, ref interpolatedAvatars, fromClientIndex, toClientIndex );
                }
            }
        }

        // apply guest avatar state at render time with interpolation

        {
            Context context = guestContext;

            Context.NetworkData connectionData = context.GetClientData();

            int fromClientIndex = 0;
            int toClientIndex = 1;

            int numInterpolatedAvatarStates;
            ushort avatarResetSequence;
            if ( connectionData.jitterBuffer.GetInterpolatedAvatars( ref interpolatedAvatars, out numInterpolatedAvatarStates, out avatarResetSequence ) )
            {
                if ( avatarResetSequence == context.resetId)
                {
                    context.ApplyAvatarUpdates( numInterpolatedAvatarStates, ref interpolatedAvatars, fromClientIndex, toClientIndex );
                }
            }
        }

        // advance jitter buffer time

        guestContext.GetClientData().jitterBuffer.AdvanceTime( Time.deltaTime );

        for ( int i = 1; i < Constants.MaxClients; ++i )
        {
            hostContext.GetServerData( i ).jitterBuffer.AdvanceTime( Time.deltaTime );
        }
    }

    new void FixedUpdate()
    {
        var hands = localAvatar.GetComponent<Hands>();

        bool reset = Input.GetKey( "space" ) || ( hands.IsPressingIndex() && hands.IsPressingX() );

        if ( reset )
        {
            hostContext.Reset();
            hostContext.resetId++;
        }

        if ( Input.GetKey( "1" ) || hands.IsPressingX() )
        {
            SwitchToHostContext();
        }
        else if ( Input.GetKey( "2" ) || hands.IsPressingY() )
        {
            SwitchToGuestContext();
        }

        MirrorLocalAvatarToRemote();

        hostContext.UpdateSleep();
        guestContext.UpdateSleep();

        byte[] serverToClientPacketData = GenerateStateUpdatePacket( hostContext, hostContext.GetServerData( 1 ), 0, 1, (float) ( physicsTime - renderTime ) );

        simulator.SendPacket( 0, 1, serverToClientPacketData );

        byte[] clientToServerPacketData = GenerateStateUpdatePacket( guestContext, guestContext.GetClientData(), 1, 0, (float) ( physicsTime - renderTime ) );

        simulator.SendPacket( 1, 0, clientToServerPacketData );

        simulator.AdvanceTime( frame * 1.0 / Constants.PhysicsFrameRate );

        while ( true )
        {
            int from, to;

            byte[] packetData = simulator.ReceivePacket( out from, out to );

            if ( packetData == null )
                break;

            Context context = GetContext( to );

            if ( to == 0 )
            {
                Assert.IsTrue( from >= 1 );
                Assert.IsTrue( from < Constants.MaxClients );

                if ( isJitterBufferEnabled )
                {
                    AddUpdatePacketToJitterBuffer( context, context.GetServerData( from ), packetData );
                }
                else
                {
                    ProcessStateUpdatePacket( context, context.GetServerData( from ), packetData, from, to );
                }
            }
            else
            {
                Assert.IsTrue( from == 0 );

                if ( isJitterBufferEnabled )
                {
                    AddUpdatePacketToJitterBuffer( context, context.GetClientData(), packetData );
                }
                else
                {
                    ProcessStateUpdatePacket( context, context.GetClientData(), packetData, from, to );
                }
            }
        }

        // process packet from host jitter buffer

        for ( int from = 1; from < Constants.MaxClients; ++from )
        {
            const int to = 0;

            Context context = GetContext( to );

            ProcessUpdateFromJitterBuffer( context, context.GetServerData( from ), from, to, isJitterBufferEnabled );
        }

        // process packet from guest jitter buffer

        if ( isJitterBufferEnabled )
        {
            const int from = 0;
            const int to = 1;

            Context context = GetContext( to );

            ProcessUpdateFromJitterBuffer( context, context.GetClientData(), from, to, isJitterBufferEnabled );
        }

        // advance host remote frame number for each connected client

        for ( int i = 1; i < Constants.MaxClients; ++i )
        {
            Context context = GetContext( 0 );

            Context.NetworkData connectionData = context.GetServerData( i );

            if ( !connectionData.isFirstPacket )
                connectionData.frame++;
        }

        // advance guest remote frame number
        {
            Context context = GetContext( 1 );

            Context.NetworkData connectionData = context.GetClientData();

            if ( !connectionData.isFirstPacket )
                connectionData.frame++;
        }

        hostContext.UpdateSleep();
        guestContext.UpdateSleep();

        ProcessAcks();

        base.FixedUpdate();
    }

    void ProcessAcks()
    {
        Profiler.BeginSample( "Process Acks" );
        {
            // host context
            {
                Context context = GetContext( 0 );

                if ( context )
                {
                    for ( int i = 1; i < Constants.MaxClients; ++i )
                    {
                        Context.NetworkData connectionData = context.GetServerData( i );

                        ProcessAcksForConnection( context, connectionData );
                    }
                }
            }

            // guest contexts

            for ( int clientIndex = 1; clientIndex < Constants.MaxClients; ++clientIndex )
            {
                Context context = GetContext( clientIndex );

                if ( !context )
                    continue;

                Context.NetworkData connectionData = context.GetClientData();

                ProcessAcksForConnection( context, connectionData );
            }
        }

        Profiler.EndSample();
    }

    public byte[] GenerateStateUpdatePacket( Context context, Context.NetworkData connectionData, int fromClientIndex, int toClientIndex, float avatarSampleTimeOffset = 0.0f )
    {
        Profiler.BeginSample( "GenerateStateUpdatePacket" );

        int maxStateUpdates = Math.Min( Constants.MaxCubes, Constants.MaxStateUpdates );

        int numStateUpdates = maxStateUpdates;

        context.UpdateCubePriorities();

        context.GetCubeUpdates( connectionData, ref numStateUpdates, ref cubeIds, ref cubes );
    var writePacketHeader = new Network.PacketHeader {
      frame = (uint)frame,
      resetId = context.resetId,
      timeOffset = 0
    };
    connectionData.acking.AddUnackedPackets(ref writePacketHeader);

        writePacketHeader.timeOffset = avatarSampleTimeOffset;

        writePacketHeader.frame = (uint) frame;

        writePacketHeader.resetId = context.resetId;

        DetermineNotChangedAndDeltas( context, connectionData, writePacketHeader.id, numStateUpdates, ref cubeIds, ref notChanged, ref hasDelta, ref baselineIds, ref cubes, ref cubeDeltas );

        DeterminePrediction( context, connectionData, writePacketHeader.id, numStateUpdates, ref cubeIds, ref notChanged, ref hasDelta, ref perfectPrediction, ref hasPredictionDelta, ref baselineIds, ref cubes, ref cubePredictions );

        int numAvatarStates = 0;

        if ( fromClientIndex == 0 )
        {
            // server -> client: send avatar state for other clients only

            numAvatarStates = 0;

            for ( int i = 0; i < Constants.MaxClients; ++i )
            {
                if ( i == toClientIndex )
                    continue;

                if ( currentContext == GetContext( i ) )
                {
                   // grab state from the local avatar.

                    localAvatar.GetComponent<Hands>().GetState( out avatars[numAvatarStates] );
                    numAvatarStates++;
                }
                else
                {
                    // grab state from a remote avatar.

                    var remoteAvatar = context.GetAvatar( i );
                    if ( remoteAvatar )
                    {
                        remoteAvatar.GetAvatarState( out avatars[numAvatarStates] );
                        numAvatarStates++;
                    }
                }
            }
        }
        else
        {
            // client -> server: send avatar state for this client only

            numAvatarStates = 1;

            if ( currentContext == GetContext( fromClientIndex ) )
            {
                localAvatar.GetComponent<Hands>().GetState( out avatars[0] );
            }
            else
            {
                GetContext( fromClientIndex ).GetAvatar( fromClientIndex ).GetAvatarState( out avatars[0] );
            }
        }

        for ( int i = 0; i < numAvatarStates; ++i )
            AvatarState.Quantize( ref avatars[i], out avatarsQuantized[i] );

        WriteUpdatePacket( ref writePacketHeader, numAvatarStates, ref avatarsQuantized, numStateUpdates, ref cubeIds, ref notChanged, ref hasDelta, ref perfectPrediction, ref hasPredictionDelta, ref baselineIds, ref cubes, ref cubeDeltas, ref cubePredictions );

        byte[] packetData = writeStream.GetData();

        // add the sent cube states to the send delta buffer

        AddPacket( ref connectionData.sendBuffer, writePacketHeader.id, context.resetId, numStateUpdates, ref cubeIds, ref cubes );

        // reset cube priority for the cubes that were included in the packet (so other cubes have a chance to be sent...)

        context.ResetCubePriority( connectionData, numStateUpdates, cubeIds );

        Profiler.EndSample();

        return packetData;
    }

    public void ProcessStateUpdatePacket( Context context, Context.NetworkData connectionData, byte[] packetData, int fromClientIndex, int toClientIndex )
    {
        Profiler.BeginSample( "ProcessStateUpdatePacket" );

        int readNumAvatarStates = 0;
        int readNumStateUpdates = 0;

        Network.PacketHeader readPacketHeader;

        if ( ReadUpdatePacket( packetData, out readPacketHeader, out readNumAvatarStates, ref readAvatarsQuantized, out readNumStateUpdates, ref readCubeIds, ref readNotChanged, ref readHasDelta, ref readPerfectPrediction, ref readHasPredictionDelta, ref readBaselineIds, ref readCubes, ref readCubeDeltas, ref readPredictionDeltas ) )
        {
            // unquantize avatar states

            for ( int i = 0; i < readNumAvatarStates; ++i )
                AvatarState.Unquantize( ref readAvatarsQuantized[i], out readAvatars[i] );

            // reset sequence handling

            if ( fromClientIndex == 0 )
            {
                // server -> client

                // Ignore updates from before the last reset.
                if ( Network.Util.IdGreaterThan( context.resetId, readPacketHeader.resetId ) )
                    return;

                // Reset if the server reset sequence is more recent than ours.
                if ( Network.Util.IdGreaterThan( readPacketHeader.resetId, context.resetId) )
                {
                    context.Reset();
                    context.resetId = readPacketHeader.resetId ;
                }
            }
            else
            {
                // server -> client

                // Ignore any updates from the client with a different reset sequence #
                if ( context.resetId != readPacketHeader.resetId )
                    return;
            }

            // decode the predicted cube states from baselines

            DecodePrediction( connectionData.receiveBuffer, context.resetId, readPacketHeader.id, readNumStateUpdates, ref readCubeIds, ref readPerfectPrediction, ref readHasPredictionDelta, ref readBaselineIds, ref readCubes, ref readPredictionDeltas );

            // decode the not changed and delta cube states from baselines

            DecodeNotChangedAndDeltas( connectionData.receiveBuffer, context.resetId, readNumStateUpdates, ref readCubeIds, ref readNotChanged, ref readHasDelta, ref readBaselineIds, ref readCubes, ref readCubeDeltas );

            // add the cube states to the receive delta buffer

            AddPacket( ref connectionData.receiveBuffer, readPacketHeader.id, context.resetId, readNumStateUpdates, ref readCubeIds, ref readCubes );

            // apply the state updates to cubes

            context.ApplyCubeUpdates( readNumStateUpdates, ref readCubeIds, ref readCubes, fromClientIndex, toClientIndex, isJitterBufferEnabled );

            // apply avatar state updates

            context.ApplyAvatarUpdates( readNumAvatarStates, ref readAvatars, fromClientIndex, toClientIndex );

            // process the packet header

            connectionData.acking.AckPackets( ref readPacketHeader );
        }

        Profiler.EndSample();
    }

    void MirrorLocalAvatarToRemote()
    {
        Profiler.BeginSample( "MirrorLocalAvatarToRemote" );

        // Mirror the local avatar onto its remote avatar on the current context.
        AvatarState avatarState;
        localAvatar.GetComponent<Hands>().GetState( out avatarState );
        currentContext.GetAvatar( currentContext.clientId).ApplyAvatarPose( ref avatarState );

        Profiler.EndSample();
    }
}
