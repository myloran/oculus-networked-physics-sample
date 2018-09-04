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

public class JitterBufferEntry
{
    public Network.PacketHeader packetHeader;
    public int numAvatarStates = 0;
    public int numStateUpdates = 0;
    public int[] cubeIds = new int[Constants.NumCubes];
    public bool[] notChanged = new bool[Constants.NumCubes];
    public bool[] hasDelta = new bool[Constants.NumCubes];
    public bool[] perfectPrediction = new bool[Constants.NumCubes];
    public bool[] hasPredictionDelta = new bool[Constants.NumCubes];
    public ushort[] baselineSequence = new ushort[Constants.NumCubes];
    public CubeState[] cubeState = new CubeState[Constants.NumCubes];
    public CubeDelta[] cubeDelta = new CubeDelta[Constants.NumCubes];
    public CubeDelta[] predictionDelta = new CubeDelta[Constants.NumCubes];
    public AvatarState[] avatarState = new AvatarState[Constants.MaxClients];
    public AvatarStateQuantized[] avatarStateQuantized = new AvatarStateQuantized[Constants.MaxClients];
}

public class JitterBuffer
{
    double time;
    long initial_frame;
    bool interpolating;
    long interpolation_start_frame;
    double interpolation_start_time;
    long interpolation_end_frame;
    double interpolation_end_time;
    Network.SequenceBuffer32<JitterBufferEntry> sequenceBuffer = new Network.SequenceBuffer32<JitterBufferEntry>( Constants.JitterBufferSize );

    public JitterBuffer()
    {
        Reset();
    }

    public void Reset()
    {
        time = -1.0;
        initial_frame = 0;
        interpolating = false;
        interpolation_start_frame = 0;
        interpolation_start_time = 0.0;
        interpolation_end_frame = 0;
        interpolation_end_time = 0.0;
        sequenceBuffer.Reset();
    }

    public bool AddUpdatePacket( byte[] packetData, DeltaBuffer receiveDeltaBuffer, ushort resetSequence, out long packetFrameNumber )
    {
        Network.PacketHeader packetHeader;
        ReadStateUpdatePacketHeader( packetData, out packetHeader );

        packetFrameNumber = packetHeader.frame;

        int entryIndex = sequenceBuffer.Insert( packetHeader.frame );
        if ( entryIndex < 0 )
        {
            return false;
        }

        bool result = true;

        Profiler.BeginSample( "ProcessStateUpdatePacket" );

        JitterBufferEntry entry = sequenceBuffer.Entries[entryIndex];

        if ( ReadUpdatePacket( packetData, out entry.packetHeader, out entry.numAvatarStates, ref entry.avatarStateQuantized, out entry.numStateUpdates, ref entry.cubeIds, ref entry.notChanged, ref entry.hasDelta, ref entry.perfectPrediction, ref entry.hasPredictionDelta, ref entry.baselineSequence, ref entry.cubeState, ref entry.cubeDelta, ref entry.predictionDelta ) )
        {
            for ( int i = 0; i < entry.numAvatarStates; ++i )
                AvatarState.Unquantize( ref entry.avatarStateQuantized[i], out entry.avatarState[i] );

            DecodePrediction( receiveDeltaBuffer, resetSequence, entry.packetHeader.sequence, entry.numStateUpdates, ref entry.cubeIds, ref entry.perfectPrediction, ref entry.hasPredictionDelta, ref entry.baselineSequence, ref entry.cubeState, ref entry.predictionDelta );

            DecodeNotChangedAndDeltas( receiveDeltaBuffer, resetSequence, entry.numStateUpdates, ref entry.cubeIds, ref entry.notChanged, ref entry.hasDelta, ref entry.baselineSequence, ref entry.cubeState, ref entry.cubeDelta );
        }
        else
        {
            sequenceBuffer.Remove( packetHeader.frame );

            result = false;
        }

        Profiler.EndSample();

        return result;
    }

    public JitterBufferEntry GetEntry( uint frameNumber )
    {
        int entryIndex = sequenceBuffer.Find( frameNumber );
        if ( entryIndex == -1 )
            return null;
        return sequenceBuffer.Entries[entryIndex];
    }

    public void Start( long initialFrame )
    {
        time = 0.0;
        initial_frame = initialFrame;
        interpolating = false;
    }

    public void AdvanceTime( float deltaTime )
    {
        Assert.IsTrue( deltaTime >= 0.0f );

        if ( time < 0 )
            return;

        time += deltaTime;
    }

    static T Clamp<T>( T value, T min, T max ) where T : System.IComparable<T>
    {
        if ( value.CompareTo( max ) > 0 )
            return max;
        else if ( value.CompareTo( min ) < 0 )
            return min;
        return value;
    }

    public bool GetInterpolatedAvatar( ref AvatarState[] output, out int numOutputAvatarStates, out ushort resetSequence )
    {
        numOutputAvatarStates = 0;
        resetSequence = 0;

        // if interpolation frame is negative, it's too early to display anything

        double interpolation_frame = initial_frame + time * Constants.PhysicsFrameRate;

        if ( interpolation_frame < 0.0 )
            return false;

        // if we are interpolating but the interpolation start frame is too old,
        // go back to the not interpolating state, so we can find a new start point.

        const int n = 16;

        if ( interpolating )
        {
            long frame = (long) Math.Floor( interpolation_frame );

            if ( frame - interpolation_start_frame > n )
                interpolating = false;
        }
        
        // if not interpolating, attempt to find an interpolation start point. 
        // if start point exists, go into interpolating mode and set end point to start point
        // so we can reuse code below to find a suitable end point on first time through.
        // if no interpolation start point is found, return.

        if ( !interpolating )
        {
            long current_frame = (uint) Math.Floor( interpolation_frame );

            for ( long frame = current_frame + 1; ( frame > current_frame - n ) && ( frame >= 0 ); frame-- )
            {
                JitterBufferEntry entry = GetEntry( (uint) frame );

                if ( entry != null )
                {
                    double avatar_sample_time = ( frame - initial_frame ) * ( 1.0 / Constants.PhysicsFrameRate ) + entry.packetHeader.timeOffset;
                    
                    if ( time >= avatar_sample_time && time <= avatar_sample_time + ( 1.0f / Constants.PhysicsFrameRate ) )
                    {
                        interpolation_start_frame = frame;
                        interpolation_end_frame = frame;

                        interpolation_start_time = avatar_sample_time;
                        interpolation_end_time = avatar_sample_time;

                        interpolating = true;
                    }
                }
            }
        }

        if ( !interpolating )
            return false;

        Assert.IsTrue( time >= interpolation_start_time );

        // if current time is >= end time, we need to start a new interpolation
        // from the previous end time to the next sample that exists up to n samples ahead.

        if ( time >= interpolation_end_time )
        {
            interpolation_start_frame = interpolation_end_frame;
            interpolation_start_time = interpolation_end_time;

            for ( int i = 0; i < n; ++i )
            {
                JitterBufferEntry entry = GetEntry( (uint) ( interpolation_start_frame + 1 + i ) );

                if ( entry != null )
                {
                    double avatar_sample_time = ( interpolation_start_frame + 1 + i - initial_frame ) * ( 1.0 / Constants.PhysicsFrameRate ) + entry.packetHeader.timeOffset;

                    if ( avatar_sample_time >= time )
                    {
                        interpolation_end_frame = interpolation_start_frame + 1 + i;
                        interpolation_end_time = avatar_sample_time + ( 1.0 / Constants.PhysicsFrameRate );
                        break;
                    }
                }
            }
        }

        // if current time is still > end time, we couldn't start a new interpolation so return.

        if ( time > interpolation_end_time )
            return false;

        // we are in a valid interpolation, calculate t by looking at current time 
        // relative to interpolation start/end times and perform the interpolation.

        float t = (float) Clamp( ( time - interpolation_start_time ) / ( interpolation_end_time - interpolation_start_time ), 0.0, 1.0 );

        JitterBufferEntry a = GetEntry( (uint) ( interpolation_start_frame ) );
        JitterBufferEntry b = GetEntry( (uint) ( interpolation_end_frame ) );

        for ( int i = 0; i < a.numAvatarStates; ++i )
        {
            for ( int j = 0; j < b.numAvatarStates; ++j )
            {
                if ( a.avatarState[i].clientId == b.avatarState[j].clientId )
                {
                    AvatarState.Interpolate( ref a.avatarState[i], ref b.avatarState[j], out output[numOutputAvatarStates], t );  
                    numOutputAvatarStates++;
                }
            }
        }

        resetSequence = a.packetHeader.resetSequence;

        return true;
    }

    Network.ReadStream readStream = new Network.ReadStream();

    PacketSerializer packetSerializer = new PacketSerializer();

    bool ReadStateUpdatePacketHeader( byte[] packetData, out Network.PacketHeader packetHeader )
    {
        Profiler.BeginSample( "ReadStateUpdatePacketHeader" );

        readStream.Start( packetData );

        bool result = true;

        try
        {
            packetSerializer.ReadStateUpdatePacketHeader( readStream, out packetHeader );
        }
        catch ( Network.SerializeException )
        {
            Debug.Log( "error: failed to read state update packet header" );

            packetHeader.sequence = 0;
            packetHeader.ack = 0;
            packetHeader.ackBits = 0;
            packetHeader.frame = 0;
            packetHeader.resetSequence = 0;
            packetHeader.timeOffset = 0.0f;

            result = false;
        }

        readStream.Finish();

        Profiler.EndSample();

        return result;
    }

    bool ReadUpdatePacket( byte[] packetData, out Network.PacketHeader packetHeader, out int numAvatarStates, ref AvatarStateQuantized[] avatarState, out int numStateUpdates, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta, ref CubeDelta[] predictionDelta )
    {
        Profiler.BeginSample( "ReadStateUpdatePacket" );

        readStream.Start( packetData );

        bool result = true;

        try
        {
            packetSerializer.ReadUpdatePacket( readStream, out packetHeader, out numAvatarStates, avatarState, out numStateUpdates, cubeIds, notChanged, hasDelta, perfectPrediction, hasPredictionDelta, baselineSequence, cubeState, cubeDelta, predictionDelta );
        }
        catch ( Network.SerializeException )
        {
            Debug.Log( "error: failed to read state update packet" );

            packetHeader.sequence = 0;
            packetHeader.ack = 0;
            packetHeader.ackBits = 0;
            packetHeader.frame = 0;
            packetHeader.resetSequence = 0;
            packetHeader.timeOffset = 0.0f;

            numAvatarStates = 0;
            numStateUpdates = 0;

            result = false;
        }

        readStream.Finish();

        Profiler.EndSample();

        return result;
    }

    bool DecodePrediction( DeltaBuffer deltaBuffer, ushort currentSequence, ushort resetSequence, int numCubes, ref int[] cubeIds, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] predictionDelta )
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

    bool DecodeNotChangedAndDeltas( DeltaBuffer deltaBuffer, ushort resetSequence, int numCubes, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta )
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
}
