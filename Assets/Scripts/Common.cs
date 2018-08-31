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
using Oculus.Platform;
using Oculus.Platform.Models;

public class Common: MonoBehaviour
{
    public const int ConnectTimeout = 15;

    public const int ConnectionTimeout = 5;

    public GameObject localAvatar;

    protected AvatarState[] interpolatedAvatarState = new AvatarState[Constants.MaxClients];

    protected bool enableJitterBuffer = true;

    protected long frameNumber = 0;

    protected double renderTime = 0.0;
    protected double physicsTime = 0.0;

    protected int[] cubeIds = new int[Constants.NumCubes];
    protected bool[] notChanged = new bool[Constants.NumCubes];
    protected bool[] hasDelta = new bool[Constants.NumCubes];
    protected bool[] perfectPrediction = new bool[Constants.NumCubes];
    protected bool[] hasPredictionDelta = new bool[Constants.NumCubes];
    protected ushort[] baselineSequence = new ushort[Constants.NumCubes];
    protected CubeState[] cubeState = new CubeState[Constants.NumCubes];
    protected CubeDelta[] cubeDelta = new CubeDelta[Constants.NumCubes];
    protected CubeDelta[] predictionDelta = new CubeDelta[Constants.NumCubes];
    protected AvatarState[] avatarState = new AvatarState[Constants.MaxClients];
    protected AvatarStateQuantized[] avatarStateQuantized = new AvatarStateQuantized[Constants.MaxClients];

    protected int[] readCubeIds = new int[Constants.NumCubes];
    protected bool[] readNotChanged = new bool[Constants.NumCubes];
    protected bool[] readHasDelta = new bool[Constants.NumCubes];
    protected bool[] readPerfectPrediction = new bool[Constants.NumCubes];
    protected bool[] readHasPredictionDelta = new bool[Constants.NumCubes];
    protected ushort[] readBaselineSequence = new ushort[Constants.NumCubes];
    protected CubeState[] readCubeState = new CubeState[Constants.NumCubes];
    protected CubeDelta[] readCubeDelta = new CubeDelta[Constants.NumCubes];
    protected CubeDelta[] readPredictionDelta = new CubeDelta[Constants.NumCubes];
    protected AvatarState[] readAvatarState = new AvatarState[Constants.MaxClients];
    protected AvatarStateQuantized[] readAvatarStateQuantized = new AvatarStateQuantized[Constants.MaxClients];

    protected uint[] packetBuffer = new uint[Constants.MaxPacketSize / 4];

    protected Network.ReadStream readStream = new Network.ReadStream();
    protected Network.WriteStream writeStream = new Network.WriteStream();

    protected PacketSerializer packetSerializer = new PacketSerializer();

    protected ushort[] acks = new ushort[Network.Connection.MaximumAcks];

    protected Network.Simulator networkSimulator = new Network.Simulator();

    protected class ServerInfo
    {
        public bool[] clientConnected = new bool[Constants.MaxClients];
        public ulong[] clientUserId = new ulong[Constants.MaxClients];
        public string[] clientUserName = new string[Constants.MaxClients];

        public void Clear()
        {
            for ( int i = 0; i < Constants.MaxClients; ++i )
            {
                clientConnected[i] = false;
                clientUserId[i] = 0;
                clientUserName[i] = "";
            }
        }

        public void CopyFrom( ServerInfo other )
        {
            for ( int i = 0; i < Constants.MaxClients; ++i )
            {
                clientConnected[i] = other.clientConnected[i];
                clientUserId[i] = other.clientUserId[i];
                clientUserName[i] = other.clientUserName[i];
            }
        }

        public int FindClientByUserId( ulong userId )
        {
            for ( int i = 0; i < Constants.MaxClients; ++i )
            {
                if ( clientConnected[i] && clientUserId[i] == userId )
                    return i;
            }

            return -1;
        }

        public void Print()
        {
            for ( int i = 0; i < Constants.MaxClients; ++i )
            {
                if ( clientConnected[i] )
                {
                    Debug.Log( i + ": " + clientUserName[i] + " [" + clientUserId[i] + "]" );
                }
                else
                {
                    Debug.Log( i + ": (not connected)" );
                }
            }
        }
    };

    protected ServerInfo serverInfo = new ServerInfo();

    protected void Start()
    {
        Debug.Log( "Running Tests" );

        Tests.RunTests();
    }

    bool wantsToShutdown = false;

    protected virtual void OnQuit()
    {
        // override this
    }

    protected virtual bool ReadyToShutdown()
    {
        return true;
    }

    protected void Update()
    {
        if ( Input.GetKeyDown( "backspace" ) )
        {
            enableJitterBuffer = !enableJitterBuffer;

            if ( enableJitterBuffer )
            {
                Debug.Log( "Enabled jitter buffer" );
            }
            else
            {
                Debug.Log( "Disabled jitter buffer" );
            }
        }

        if ( Input.GetKeyDown( KeyCode.Escape ) )
        {
            Debug.Log( "User quit the application (ESC)" );

            wantsToShutdown = true;

            OnQuit();
        }

        if ( wantsToShutdown && ReadyToShutdown() )
        {
            Debug.Log( "Shutting down" );

            UnityEngine.Application.Quit();

            wantsToShutdown = false;
        }

        renderTime += Time.deltaTime;
    }

    protected void FixedUpdate()
    {
        physicsTime += 1.0 / Constants.PhysicsFrameRate;

        frameNumber++;
    }

    protected void UpdateJitterBuffer( Context context, Context.ConnectionData connectionData )
    {
        if ( !connectionData.isFirstPacket )
            connectionData.frame++;
    }

    protected void AddStateUpdatePacketToJitterBuffer( Context context, Context.ConnectionData connectionData, byte[] packetData )
    {
        long packetFrameNumber;

        if ( connectionData.jitterBuffer.AddStateUpdatePacket( packetData, connectionData.receiveBuffer, context.GetResetSequence(), out packetFrameNumber ) )
        {
            if ( connectionData.isFirstPacket )
            {
                connectionData.isFirstPacket = false;
                connectionData.frame = packetFrameNumber - Constants.NumJitterBufferFrames;
                connectionData.jitterBuffer.Start( connectionData.frame );
            }
        }
    }

    protected void ProcessStateUpdateFromJitterBuffer( Context context, Context.ConnectionData connectionData, int fromClientIndex, int toClientIndex, bool applySmoothing = true )
    {
        if ( connectionData.frame < 0 )
            return;

        JitterBufferEntry entry = connectionData.jitterBuffer.GetEntry( (uint) connectionData.frame );
        if ( entry == null )
            return;

        if ( fromClientIndex == 0 )
        {
            // server -> client

            // Ignore updates from before the last reset.
            if ( Network.Util.SequenceGreaterThan( context.GetResetSequence(), entry.packetHeader.resetSequence ) )
                return;

            // Reset if the server reset sequence is more recent than ours.
            if ( Network.Util.SequenceGreaterThan( entry.packetHeader.resetSequence, context.GetResetSequence() ) )
            {
                context.Reset();
                context.SetResetSequence( entry.packetHeader.resetSequence );
            }
        }
        else
        {
            // client -> server

            // Ignore any updates from the client with a different reset sequence #
            if ( context.GetResetSequence() != entry.packetHeader.resetSequence )
                return;
        }

        // add the cube states to the receive delta buffer

        AddPacketToDeltaBuffer( ref connectionData.receiveBuffer, entry.packetHeader.sequence, context.GetResetSequence(), entry.numStateUpdates, ref entry.cubeIds, ref entry.cubeState );

        // apply the state updates to cubes

        context.ApplyCubeUpdates( entry.numStateUpdates, ref entry.cubeIds, ref entry.cubeState, fromClientIndex, toClientIndex, applySmoothing );

        // process the packet header (handles acks)

        connectionData.connection.ProcessPacketHeader( ref entry.packetHeader );
    }

    protected bool WriteServerInfoPacket( bool[] clientConnected, ulong[] clientUserId, string[] clientUserName )
    {
        Profiler.BeginSample( "WriteServerInfoPacket" );

        writeStream.Start( packetBuffer );

        bool result = true;

        try
        {
            packetSerializer.WriteServerInfoPacket( writeStream, clientConnected, clientUserId, clientUserName );

            writeStream.Finish();
        }
        catch ( Network.SerializeException )
        {
            Debug.Log( "error: failed to write server info packet" );
            result = false;
        }

        Profiler.EndSample();

        return result;
    }

    protected bool ReadServerInfoPacket( byte[] packetData, bool[] clientConnected, ulong[] clientUserId, string[] clientUserName )
    {
        Profiler.BeginSample( "ReadServerInfoPacket" );

        readStream.Start( packetData );

        bool result = true;

        try
        {
            packetSerializer.ReadServerInfoPacket( readStream, clientConnected, clientUserId, clientUserName );
        }
        catch ( Network.SerializeException )
        {
            Debug.Log( "error: failed to read server info packet" );
            result = false;
        }

        readStream.Finish();

        Profiler.EndSample();

        return result;
    }

    protected bool WriteStateUpdatePacket( ref Network.PacketHeader packetHeader, int numAvatarStates, ref AvatarStateQuantized[] avatarState, int numStateUpdates, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta, ref CubeDelta[] predictionDelta )
    {
        Profiler.BeginSample( "WriteStateUpdatePacket" );

        writeStream.Start( packetBuffer );

        bool result = true;

        try
        {
            packetSerializer.WriteStateUpdatePacket( writeStream, ref packetHeader, numAvatarStates, avatarState, numStateUpdates, cubeIds, notChanged, hasDelta, perfectPrediction, hasPredictionDelta, baselineSequence, cubeState, cubeDelta, predictionDelta );

            writeStream.Finish();
        }
        catch ( Network.SerializeException )
        {
            Debug.Log( "error: failed to write state update packet packet" );
            result = false;
        }

        Profiler.EndSample();

        return result;
    }

    protected bool ReadStateUpdatePacket( byte[] packetData, out Network.PacketHeader packetHeader, out int numAvatarStates, ref AvatarStateQuantized[] avatarState, out int numStateUpdates, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta, ref CubeDelta[] predictionDelta )
    {
        Profiler.BeginSample( "ReadStateUpdatePacket" );

        readStream.Start( packetData );

        bool result = true;

        try
        {
            packetSerializer.ReadStateUpdatePacket( readStream, out packetHeader, out numAvatarStates, avatarState, out numStateUpdates, cubeIds, notChanged, hasDelta, perfectPrediction, hasPredictionDelta, baselineSequence, cubeState, cubeDelta, predictionDelta );
        }
        catch ( Network.SerializeException )
        {
            Debug.Log( "error: failed to read state update packet" );

            packetHeader.sequence = 0;
            packetHeader.ack = 0;
            packetHeader.ack_bits = 0;
            packetHeader.frameNumber = 0;
            packetHeader.resetSequence = 0;
            packetHeader.avatarSampleTimeOffset = 0.0f;

            numAvatarStates = 0;
            numStateUpdates = 0;

            result = false;
        }

        readStream.Finish();

        Profiler.EndSample();

        return result;
    }

    protected void AddPacketToDeltaBuffer( ref DeltaBuffer deltaBuffer, ushort sequence, ushort resetSequence, int numCubes, ref int[] cubeIds, ref CubeState[] cubeState )
    {
        Profiler.BeginSample( "AddPacketToDeltaBuffer" );

        deltaBuffer.AddPacket( sequence, resetSequence );

        for ( int i = 0; i < numCubes; ++i )
        {
            deltaBuffer.AddCubeState( sequence, cubeIds[i], ref cubeState[i] );
        }

        Profiler.EndSample();
    }

    protected void DetermineNotChangedAndDeltas( Context context, Context.ConnectionData connectionData, ushort currentSequence, int numCubes, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta )
    {
        Profiler.BeginSample( "DeterminedNotChangedAndDeltas" );

#if !DISABLE_DELTA_COMPRESSION
        CubeState baselineCubeState = CubeState.defaults;
#endif // #if !DISABLE_DELTA_COMPRESSION

        for ( int i = 0; i < numCubes; ++i )
        {
            notChanged[i] = false;
            hasDelta[i] = false;

#if !DISABLE_DELTA_COMPRESSION

#if DEBUG_DELTA_COMPRESSION
            cubeDelta[i].absolute_position_x = cubeState[i].position_x;
            cubeDelta[i].absolute_position_y = cubeState[i].position_y;
            cubeDelta[i].absolute_position_z = cubeState[i].position_z;
#endif // #if DEBUG_DELTA_COMPRESSION

            if ( context.GetAck( connectionData, cubeIds[i], ref baselineSequence[i], context.GetResetSequence(), ref baselineCubeState ) )
            {
                if ( Network.Util.BaselineDifference( currentSequence, baselineSequence[i] ) > Constants.MaxBaselineDifference )
                {
                    // baseline is too far behind => send the cube state absolute.
                    continue;
                }

                if ( baselineCubeState.Equals( cubeState[i] ) )
                {
                    notChanged[i] = true;
                }
                else
                {
                    hasDelta[i] = true;

                    cubeDelta[i].positionX = cubeState[i].positionX - baselineCubeState.positionX;
                    cubeDelta[i].positionY = cubeState[i].positionY - baselineCubeState.positionY;
                    cubeDelta[i].positionZ = cubeState[i].positionZ - baselineCubeState.positionZ;

                    cubeDelta[i].linearVelocityX = cubeState[i].linearVelocityX - baselineCubeState.linearVelocityX;
                    cubeDelta[i].linearVelocityY = cubeState[i].linearVelocityY - baselineCubeState.linearVelocityY;
                    cubeDelta[i].linearVelocityZ = cubeState[i].linearVelocityZ - baselineCubeState.linearVelocityZ;

                    cubeDelta[i].angularVelocityX = cubeState[i].angularVelocityX - baselineCubeState.angularVelocityX;
                    cubeDelta[i].angularVelocityY = cubeState[i].angularVelocityY - baselineCubeState.angularVelocityY;
                    cubeDelta[i].angularVelocityZ = cubeState[i].angularVelocityZ - baselineCubeState.angularVelocityZ;
                }
            }

#endif // #if !DISABLE_DELTA_COMPRESSION
        }

        Profiler.EndSample();
    }

    protected bool DecodeNotChangedAndDeltas( DeltaBuffer deltaBuffer, ushort resetSequence, int numCubes, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta )
    {
        Profiler.BeginSample( "DecodeNotChangedAndDeltas" );

        bool result = true;

#if !DISABLE_DELTA_COMPRESSION

        CubeState baselineCubeState = CubeState.defaults;

        for ( int i = 0; i < numCubes; ++i )
        {
            if ( notChanged[i] )
            {
                if ( deltaBuffer.GetCubeState( baselineSequence[i], resetSequence, cubeIds[i], ref baselineCubeState ) )
                {
#if DEBUG_DELTA_COMPRESSION
                    if ( baselineCubeState.position_x != cubeDelta[i].absolute_position_x )
                    {
                        Debug.Log( "expected " + cubeDelta[i].absolute_position_x + ", got " + baselineCubeState.position_x );
                    }
                    Assert.IsTrue( baselineCubeState.position_x == cubeDelta[i].absolute_position_x );
                    Assert.IsTrue( baselineCubeState.position_y == cubeDelta[i].absolute_position_y );
                    Assert.IsTrue( baselineCubeState.position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION

                    cubeState[i] = baselineCubeState;
                }
                else
                {
                    Debug.Log( "error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineSequence[i] + " (not changed)" );
                    result = false;
                    break;
                }
            }
            else if ( hasDelta[i] )
            {
                if ( deltaBuffer.GetCubeState( baselineSequence[i], resetSequence, cubeIds[i], ref baselineCubeState ) )
                {
                    cubeState[i].positionX = baselineCubeState.positionX + cubeDelta[i].positionX;
                    cubeState[i].positionY = baselineCubeState.positionY + cubeDelta[i].positionY;
                    cubeState[i].positionZ = baselineCubeState.positionZ + cubeDelta[i].positionZ;

#if DEBUG_DELTA_COMPRESSION
                    Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
                    Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
                    Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION

                    cubeState[i].linearVelocityX = baselineCubeState.linearVelocityX + cubeDelta[i].linearVelocityX;
                    cubeState[i].linearVelocityY = baselineCubeState.linearVelocityY + cubeDelta[i].linearVelocityY;
                    cubeState[i].linearVelocityZ = baselineCubeState.linearVelocityZ + cubeDelta[i].linearVelocityZ;

                    cubeState[i].angularVelocityX = baselineCubeState.angularVelocityX + cubeDelta[i].angularVelocityX;
                    cubeState[i].angularVelocityY = baselineCubeState.angularVelocityY + cubeDelta[i].angularVelocityY;
                    cubeState[i].angularVelocityZ = baselineCubeState.angularVelocityZ + cubeDelta[i].angularVelocityZ;
                }
                else
                {
                    Debug.Log( "error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineSequence[i] + " (delta)" );
                    result = false;
                    break;
                }
            }
        }

#endif // #if !DISABLE_DELTA_COMPRESSION

        return result;
    }

    protected void DeterminePrediction( Context context, Context.ConnectionData connectionData, ushort currentSequence, int numCubes, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] predictionDeltas )
    {
        Profiler.BeginSample( "DeterminePrediction" );

        CubeState baselineCubeState = CubeState.defaults;

        for ( int i = 0; i < numCubes; ++i )
        {
            perfectPrediction[i] = false;
            hasPredictionDelta[i] = false;

#if !DISABLE_DELTA_ENCODING

            if ( notChanged[i] )
                continue;

            if ( !hasDelta[i] )
                continue;

            if ( !cubeState[i].isActive )
                continue;

            if ( context.GetAck( connectionData, cubeIds[i], ref baselineSequence[i], context.GetResetSequence(), ref baselineCubeState ) )
            {
                if ( Network.Util.BaselineDifference( currentSequence, baselineSequence[i] ) <= Constants.MaxBaselineDifference )
                {
                    // baseline is too far behind. send the cube state absolute
                    continue;
                }

                if ( !baselineCubeState.isActive )
                {
                    // no point predicting if the cube is at rest.
                    continue;
                }

                int baseline_sequence = baselineSequence[i];
                int current_sequence = currentSequence;

                if ( current_sequence < baseline_sequence )
                    current_sequence += 65536;

                int baseline_position_x = baselineCubeState.positionX;
                int baseline_position_y = baselineCubeState.positionY;
                int baseline_position_z = baselineCubeState.positionZ;

                int baseline_linear_velocity_x = baselineCubeState.linearVelocityX;
                int baseline_linear_velocity_y = baselineCubeState.linearVelocityY;
                int baseline_linear_velocity_z = baselineCubeState.linearVelocityZ;

                int baseline_angular_velocity_x = baselineCubeState.angularVelocityX;
                int baseline_angular_velocity_y = baselineCubeState.angularVelocityY;
                int baseline_angular_velocity_z = baselineCubeState.angularVelocityZ;

                if ( current_sequence < baseline_sequence )
                    current_sequence += 65536;

                int numFrames = current_sequence - baseline_sequence;

                int predicted_position_x;
                int predicted_position_y;
                int predicted_position_z;

                int predicted_linear_velocity_x;
                int predicted_linear_velocity_y;
                int predicted_linear_velocity_z;

                int predicted_angular_velocity_x;
                int predicted_angular_velocity_y;
                int predicted_angular_velocity_z;

                Prediction.PredictBallistic( numFrames,
                                             baseline_position_x, baseline_position_y, baseline_position_z,
                                             baseline_linear_velocity_x, baseline_linear_velocity_y, baseline_linear_velocity_z,
                                             baseline_angular_velocity_x, baseline_angular_velocity_y, baseline_angular_velocity_z,
                                             out predicted_position_x, out predicted_position_y, out predicted_position_z,
                                             out predicted_linear_velocity_x, out predicted_linear_velocity_y, out predicted_linear_velocity_z,
                                             out predicted_angular_velocity_x, out predicted_angular_velocity_y, out predicted_angular_velocity_z );

                int current_position_x = cubeState[i].positionX;
                int current_position_y = cubeState[i].positionY;
                int current_position_z = cubeState[i].positionZ;

                int current_linear_velocity_x = cubeState[i].linearVelocityX;
                int current_linear_velocity_y = cubeState[i].linearVelocityY;
                int current_linear_velocity_z = cubeState[i].linearVelocityZ;

                int current_angular_velocity_x = cubeState[i].angularVelocityX;
                int current_angular_velocity_y = cubeState[i].angularVelocityY;
                int current_angular_velocity_z = cubeState[i].angularVelocityZ;

                int position_error_x = current_position_x - predicted_position_x;
                int position_error_y = current_position_y - predicted_position_y;
                int position_error_z = current_position_z - predicted_position_z;

                int linear_velocity_error_x = current_linear_velocity_x - predicted_linear_velocity_x;
                int linear_velocity_error_y = current_linear_velocity_y - predicted_linear_velocity_y;
                int linear_velocity_error_z = current_linear_velocity_z - predicted_linear_velocity_z;

                int angular_velocity_error_x = current_angular_velocity_x - predicted_angular_velocity_x;
                int angular_velocity_error_y = current_angular_velocity_y - predicted_angular_velocity_y;
                int angular_velocity_error_z = current_angular_velocity_z - predicted_angular_velocity_z;

                if ( position_error_x == 0 &&
                     position_error_y == 0 &&
                     position_error_z == 0 &&
                     linear_velocity_error_x == 0 &&
                     linear_velocity_error_y == 0 &&
                     linear_velocity_error_z == 0 &&
                     angular_velocity_error_x == 0 &&
                     angular_velocity_error_y == 0 &&
                     angular_velocity_error_z == 0 )
                {
                    perfectPrediction[i] = true;
                }
                else
                {
                    int abs_position_error_x = Math.Abs( position_error_x );
                    int abs_position_error_y = Math.Abs( position_error_y );
                    int abs_position_error_z = Math.Abs( position_error_z );

                    int abs_linear_velocity_error_x = Math.Abs( linear_velocity_error_x );
                    int abs_linear_velocity_error_y = Math.Abs( linear_velocity_error_y );
                    int abs_linear_velocity_error_z = Math.Abs( linear_velocity_error_z );

                    int abs_angular_velocity_error_x = Math.Abs( angular_velocity_error_x );
                    int abs_angular_velocity_error_y = Math.Abs( angular_velocity_error_y );
                    int abs_angular_velocity_error_z = Math.Abs( angular_velocity_error_z );

                    int total_prediction_error = abs_position_error_x +
                                                 abs_position_error_y +
                                                 abs_position_error_z +
                                                 linear_velocity_error_x +
                                                 linear_velocity_error_y +
                                                 linear_velocity_error_z +
                                                 angular_velocity_error_x +
                                                 angular_velocity_error_y +
                                                 angular_velocity_error_z;

                    int total_absolute_error = Math.Abs( cubeState[i].positionX - baselineCubeState.positionX ) +
                                               Math.Abs( cubeState[i].positionY - baselineCubeState.positionY ) +
                                               Math.Abs( cubeState[i].positionZ - baselineCubeState.positionZ ) +
                                               Math.Abs( cubeState[i].linearVelocityX - baselineCubeState.linearVelocityX ) +
                                               Math.Abs( cubeState[i].linearVelocityY - baselineCubeState.linearVelocityY ) +
                                               Math.Abs( cubeState[i].linearVelocityZ - baselineCubeState.linearVelocityZ ) +
                                               Math.Abs( cubeState[i].angularVelocityX - baselineCubeState.angularVelocityX ) +
                                               Math.Abs( cubeState[i].angularVelocityY - baselineCubeState.angularVelocityY ) +
                                               Math.Abs( cubeState[i].angularVelocityZ - baselineCubeState.angularVelocityZ );

                    if ( total_prediction_error < total_absolute_error )
                    {
                        int max_position_error = abs_position_error_x;

                        if ( abs_position_error_y > max_position_error )
                            max_position_error = abs_position_error_y;

                        if ( abs_position_error_z > max_position_error )
                            max_position_error = abs_position_error_z;

                        int max_linear_velocity_error = abs_linear_velocity_error_x;

                        if ( abs_linear_velocity_error_y > max_linear_velocity_error )
                            max_linear_velocity_error = abs_linear_velocity_error_y;

                        if ( abs_linear_velocity_error_z > max_linear_velocity_error )
                            max_linear_velocity_error = abs_linear_velocity_error_z;

                        int max_angular_velocity_error = abs_angular_velocity_error_x;

                        if ( abs_angular_velocity_error_y > max_angular_velocity_error )
                            max_angular_velocity_error = abs_angular_velocity_error_y;

                        if ( abs_angular_velocity_error_z > max_angular_velocity_error )
                            max_angular_velocity_error = abs_angular_velocity_error_z;

                        if ( max_position_error <= Constants.PositionDeltaMax &&
                             max_linear_velocity_error <= Constants.LinearVelocityDeltaMax &&
                             max_angular_velocity_error <= Constants.AngularVelocityDeltaMax )
                        {
                            hasPredictionDelta[i] = true;

                            predictionDelta[i].positionX = position_error_x;
                            predictionDelta[i].positionY = position_error_y;
                            predictionDelta[i].positionZ = position_error_z;

                            predictionDelta[i].linearVelocityX = linear_velocity_error_x;
                            predictionDelta[i].linearVelocityY = linear_velocity_error_y;
                            predictionDelta[i].linearVelocityZ = linear_velocity_error_z;

                            predictionDelta[i].angularVelocityX = angular_velocity_error_x;
                            predictionDelta[i].angularVelocityY = angular_velocity_error_y;
                            predictionDelta[i].angularVelocityZ = angular_velocity_error_z;
                        }
                    }
                }
            }
        }

#endif // #if !DISABLE_DELTA_ENCODING

        Profiler.EndSample();
    }

    protected bool DecodePrediction( DeltaBuffer deltaBuffer, ushort currentSequence, ushort resetSequence, int numCubes, ref int[] cubeIds, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] predictionDelta )
    {
        Profiler.BeginSample( "DecodePrediction" );

        CubeState baselineCubeState = CubeState.defaults;

        bool result = true;

#if !DISABLE_DELTA_ENCODING

        for ( int i = 0; i < numCubes; ++i )
        {
            if ( perfectPrediction[i] || hasPredictionDelta[i] )
            {
                if ( deltaBuffer.GetCubeState( baselineSequence[i], resetSequence, cubeIds[i], ref baselineCubeState ) )
                {
                    int baseline_sequence = baselineSequence[i];
                    int current_sequence = currentSequence;

                    if ( current_sequence < baseline_sequence )
                        current_sequence += 65536;

                    int baseline_position_x = baselineCubeState.positionX;
                    int baseline_position_y = baselineCubeState.positionY;
                    int baseline_position_z = baselineCubeState.positionZ;

                    int baseline_linear_velocity_x = baselineCubeState.linearVelocityX;
                    int baseline_linear_velocity_y = baselineCubeState.linearVelocityY;
                    int baseline_linear_velocity_z = baselineCubeState.linearVelocityZ;

                    int baseline_angular_velocity_x = baselineCubeState.angularVelocityX;
                    int baseline_angular_velocity_y = baselineCubeState.angularVelocityY;
                    int baseline_angular_velocity_z = baselineCubeState.angularVelocityZ;

                    if ( current_sequence < baseline_sequence )
                        current_sequence += 65536;

                    int numFrames = current_sequence - baseline_sequence;

                    int predicted_position_x;
                    int predicted_position_y;
                    int predicted_position_z;

                    int predicted_linear_velocity_x;
                    int predicted_linear_velocity_y;
                    int predicted_linear_velocity_z;

                    int predicted_angular_velocity_x;
                    int predicted_angular_velocity_y;
                    int predicted_angular_velocity_z;

                    Prediction.PredictBallistic( numFrames,
                                                 baseline_position_x, baseline_position_y, baseline_position_z,
                                                 baseline_linear_velocity_x, baseline_linear_velocity_y, baseline_linear_velocity_z,
                                                 baseline_angular_velocity_x, baseline_angular_velocity_y, baseline_angular_velocity_z,
                                                 out predicted_position_x, out predicted_position_y, out predicted_position_z,
                                                 out predicted_linear_velocity_x, out predicted_linear_velocity_y, out predicted_linear_velocity_z,
                                                 out predicted_angular_velocity_x, out predicted_angular_velocity_y, out predicted_angular_velocity_z );

                    if ( perfectPrediction[i] )
                    {
#if DEBUG_DELTA_COMPRESSION
                        Assert.IsTrue( predicted_position_x == cubeDelta[i].absolute_position_x );
                        Assert.IsTrue( predicted_position_y == cubeDelta[i].absolute_position_y );
                        Assert.IsTrue( predicted_position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION

                        cubeState[i].positionX = predicted_position_x;
                        cubeState[i].positionY = predicted_position_y;
                        cubeState[i].positionZ = predicted_position_z;

                        cubeState[i].linearVelocityX = predicted_linear_velocity_x;
                        cubeState[i].linearVelocityY = predicted_linear_velocity_y;
                        cubeState[i].linearVelocityZ = predicted_linear_velocity_z;

                        cubeState[i].angularVelocityX = predicted_angular_velocity_x;
                        cubeState[i].angularVelocityY = predicted_angular_velocity_y;
                        cubeState[i].angularVelocityZ = predicted_angular_velocity_z;
                    }
                    else
                    {
                        cubeState[i].positionX = predicted_position_x + predictionDelta[i].positionX;
                        cubeState[i].positionY = predicted_position_y + predictionDelta[i].positionY;
                        cubeState[i].positionZ = predicted_position_z + predictionDelta[i].positionZ;

#if DEBUG_DELTA_COMPRESSION
                        Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
                        Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
                        Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION

                        cubeState[i].linearVelocityX = predicted_linear_velocity_x + predictionDelta[i].linearVelocityX;
                        cubeState[i].linearVelocityY = predicted_linear_velocity_y + predictionDelta[i].linearVelocityY;
                        cubeState[i].linearVelocityZ = predicted_linear_velocity_z + predictionDelta[i].linearVelocityZ;

                        cubeState[i].angularVelocityX = predicted_angular_velocity_x + predictionDelta[i].angularVelocityX;
                        cubeState[i].angularVelocityY = predicted_angular_velocity_y + predictionDelta[i].angularVelocityY;
                        cubeState[i].angularVelocityZ = predicted_angular_velocity_z + predictionDelta[i].angularVelocityZ;
                    }
                }
                else
                {
                    Debug.Log( "error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineSequence[i] + " (perfect prediction and prediction delta)" );
                    result = false;
                    break;
                }
            }
        }

#endif // #if !DISABLE_DELTA_COMPRESSION

        Profiler.EndSample();

        return result;
    }

    protected void ProcessAcksForConnection( Context context, Context.ConnectionData data )
    {
        Profiler.BeginSample( "ProcessAcksForConnection" );

        int numAcks = 0;

        data.connection.GetAcks( ref acks, ref numAcks );

        for ( int i = 0; i < numAcks; ++i )
        {
            int packetNumCubeStates;
            int[] packetCubeIds;
            CubeState[] packetCubeState;

            if ( data.sendBuffer.GetPacketData( acks[i], context.GetResetSequence(), out packetNumCubeStates, out packetCubeIds, out packetCubeState ) )
            {
                for ( int j = 0; j < packetNumCubeStates; ++j )
                {
                    context.UpdateAck( data, packetCubeIds[j], acks[i], context.GetResetSequence(), ref packetCubeState[j] );
                }
            }
        }

        Profiler.EndSample();
    }

    protected void WriteDeltasToFile( System.IO.StreamWriter file, DeltaBuffer deltaBuffer, ushort sequence, ushort resetSequence, int numCubes, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta )
    {
        if ( file == null )
            return;

        CubeState baselineCubeState = CubeState.defaults;

        for ( int i = 0; i < numCubes; ++i )
        {
            if ( hasDelta[i] )
            {
                bool result = deltaBuffer.GetCubeState( baselineSequence[i], resetSequence, cubeIds[i], ref baselineCubeState );

                Assert.IsTrue( result );

                if ( result )
                {
                    file.WriteLine( sequence + "," +
                                    baselineSequence[i] + "," +
                                    cubeDelta[i].positionX + "," +
                                    cubeDelta[i].positionY + "," +
                                    cubeDelta[i].positionZ + "," + ",,," +   // <--- for backwards compatibility.
                                    cubeDelta[i].linearVelocityX + "," +    //todo: remove this and fix up the indices in "TestPrediction".
                                    cubeDelta[i].linearVelocityY + "," +
                                    cubeDelta[i].linearVelocityZ + "," +
                                    cubeDelta[i].angularVelocityX + "," +
                                    cubeDelta[i].angularVelocityY + "," +
                                    cubeDelta[i].angularVelocityZ + "," +
                                    ( baselineCubeState.isActive ? 1 : 0 ) + "," +
                                    baselineCubeState.positionX + "," +
                                    baselineCubeState.positionY + "," +
                                    baselineCubeState.positionZ + "," +
                                    baselineCubeState.rotationLargest + "," +
                                    baselineCubeState.rotationX + "," +
                                    baselineCubeState.rotationY + "," +
                                    baselineCubeState.rotationZ + "," +
                                    baselineCubeState.linearVelocityX + "," +
                                    baselineCubeState.linearVelocityY + "," +
                                    baselineCubeState.linearVelocityZ + "," +
                                    baselineCubeState.angularVelocityX + "," +
                                    baselineCubeState.angularVelocityY + "," +
                                    baselineCubeState.angularVelocityZ + "," +
                                    ( cubeState[i].isActive ? 1 : 0 ) + "," +
                                    cubeState[i].positionX + "," +
                                    cubeState[i].positionY + "," +
                                    cubeState[i].positionZ + "," +
                                    cubeState[i].rotationLargest + "," +
                                    cubeState[i].rotationX + "," +
                                    cubeState[i].rotationY + "," +
                                    cubeState[i].rotationZ + "," +
                                    cubeState[i].linearVelocityX + "," +
                                    cubeState[i].linearVelocityY + "," +
                                    cubeState[i].linearVelocityZ + "," +
                                    cubeState[i].angularVelocityX + "," +
                                    cubeState[i].angularVelocityY + "," +
                                    cubeState[i].angularVelocityZ );
                }
            }
        }

        file.Flush();
    }

    protected void WritePacketSizeToFile( System.IO.StreamWriter file, int packetBytes )
    {
        if ( file == null )
            return;

        file.WriteLine( packetBytes );

        file.Flush();
    }

    protected void InitializePlatformSDK( Oculus.Platform.Message.Callback callback )
    {
        Core.Initialize();

        Entitlements.IsUserEntitledToApplication().OnComplete( callback );
    }

    protected void JoinRoom( ulong roomId, Message<Room>.Callback callback )
    {
        Debug.Log( "Joining room " + roomId );

        Rooms.Join( roomId, true ).OnComplete( callback );
    }

    protected void LeaveRoom( ulong roomId, Message<Room>.Callback callback )
    {
        if ( roomId == 0 )
            return;

        Debug.Log( "Leaving room " + roomId );

        Rooms.Leave( roomId ).OnComplete( callback );
    }

    protected void PrintRoomDetails( Room room )
    {
        Debug.Log( "AppID: " + room.ApplicationID );
        Debug.Log( "Room ID: " + room.ID );
        Debug.Log( "Users in room: " + room.Users.Count + " / " + room.MaxUsers );
        if ( room.Owner != null )
        {
            Debug.Log( "Room owner: " + room.Owner.OculusID + " [" + room.Owner.ID + "]" );
        }
        Debug.Log( "Join Policy: " + room.JoinPolicy.ToString() );
        Debug.Log( "Room Type: " + room.Type.ToString() );
    }

    protected bool FindUserById( UserList users, ulong userId )
    {
        foreach ( var user in users )
        {
            if ( user.ID == userId )
                return true;
        }
        return false;
    }
}
