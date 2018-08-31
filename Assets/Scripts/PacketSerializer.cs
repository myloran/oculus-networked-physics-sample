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
using UnityEngine.Assertions;
using System.Collections.Generic;

public class PacketSerializer: Network.Serializer
{
    public enum PacketType
    {
        ServerInfo = 1,                     // information about players connected to the server. broadcast from server -> clients whenever a player joins or leaves the game.
        StateUpdate = 0,                    // most recent state of the world, delta encoded relative to most recent state per-object acked by the client. sent 90 times per-second.
    };

    public void WriteServerInfoPacket( Network.WriteStream stream, bool[] clientConnected, ulong[] clientUserId, string[] clientUserName )
    {
        byte packetType = (byte) PacketType.ServerInfo;

        write_bits( stream, packetType, 8 );

        for ( int i = 0; i < Constants.MaxClients; ++i )
        {
            write_bool( stream, clientConnected[i] );

            if ( !clientConnected[i] )
                continue;

            write_bits( stream, clientUserId[i], 64 );

            write_string( stream, clientUserName[i] );
        }
    }

    public void ReadServerInfoPacket( Network.ReadStream stream, bool[] clientConnected, ulong[] clientUserId, string[] clientUserName )
    {
        byte packetType = 0;

        read_bits( stream, out packetType, 8 );

        Debug.Assert( packetType == (byte) PacketType.ServerInfo );

        for ( int i = 0; i < Constants.MaxClients; ++i )
        {
            read_bool( stream, out clientConnected[i] );

            if ( !clientConnected[i] )
                continue;

            read_bits( stream, out clientUserId[i], 64 );

            read_string( stream, out clientUserName[i] );
        }
    }

    public void WriteStateUpdatePacket( Network.WriteStream stream, ref Network.PacketHeader header, int numAvatarStates, AvatarStateQuantized[] avatarState, int numStateUpdates, int[] cubeIds, bool[] notChanged, bool[] hasDelta, bool[] perfectPrediction, bool[] hasPredictionDelta, ushort[] baselineSequence, CubeState[] cubeState, CubeDelta[] cubeDelta, CubeDelta[] predictionDelta )
    {
        byte packetType = (byte) PacketType.StateUpdate;

        write_bits( stream, packetType, 8 );

        write_bits( stream, header.sequence, 16 );
        write_bits( stream, header.ack, 16 );
        write_bits( stream, header.ack_bits, 32 );
        write_bits( stream, header.frameNumber, 32 );
        write_bits( stream, header.resetSequence, 16 );
        write_float( stream, header.avatarSampleTimeOffset );

        write_int( stream, numAvatarStates, 0, Constants.MaxClients );
        for ( int i = 0; i < numAvatarStates; ++i )
        {
            write_avatar_state( stream, ref avatarState[i] );
        }

        write_int( stream, numStateUpdates, 0, Constants.MaxStateUpdates );

        for ( int i = 0; i < numStateUpdates; ++i )
        {
            write_int( stream, cubeIds[i], 0, Constants.NumCubes - 1 );

#if DEBUG_DELTA_COMPRESSION
            write_int( stream, cubeDelta[i].absolute_position_x, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
            write_int( stream, cubeDelta[i].absolute_position_y, Constants.PositionMinimumY, Constants.PositionMaximumY );
            write_int( stream, cubeDelta[i].absolute_position_z, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
#endif // #if DEBUG_DELTA_COMPRESSION

            write_int( stream, cubeState[i].authorityId, 0, Constants.MaxAuthority - 1 );
            write_bits( stream, cubeState[i].authoritySequence, 16 );
            write_bits( stream, cubeState[i].ownershipSequence, 16 );

            write_bool( stream, notChanged[i] );

            if ( notChanged[i] )
            {
                write_bits( stream, baselineSequence[i], 16 );
            }
            else
            {
                write_bool( stream, perfectPrediction[i] );

                if ( perfectPrediction[i] )
                {
                    write_bits( stream, baselineSequence[i], 16 );

                    write_bits( stream, cubeState[i].rotationLargest, 2 );
                    write_bits( stream, cubeState[i].rotationX, Constants.RotationBits );
                    write_bits( stream, cubeState[i].rotationY, Constants.RotationBits );
                    write_bits( stream, cubeState[i].rotationZ, Constants.RotationBits );
                }
                else
                {
                    write_bool( stream, hasPredictionDelta[i] );

                    if ( hasPredictionDelta[i] )
                    {
                        write_bits( stream, baselineSequence[i], 16 );

                        write_bool( stream, cubeState[i].isActive );

                        write_linear_velocity_delta( stream, predictionDelta[i].linearVelocityX, predictionDelta[i].linearVelocityY, predictionDelta[i].linearVelocityZ );

                        write_angular_velocity_delta( stream, predictionDelta[i].angularVelocityX, predictionDelta[i].angularVelocityY, predictionDelta[i].angularVelocityZ );

                        write_position_delta( stream, predictionDelta[i].positionX, predictionDelta[i].positionY, predictionDelta[i].positionZ );

                        write_bits( stream, cubeState[i].rotationLargest, 2 );
                        write_bits( stream, cubeState[i].rotationX, Constants.RotationBits );
                        write_bits( stream, cubeState[i].rotationY, Constants.RotationBits );
                        write_bits( stream, cubeState[i].rotationZ, Constants.RotationBits );
                    }
                    else
                    {
                        write_bool( stream, hasDelta[i] );

                        if ( hasDelta[i] )
                        {
                            write_bits( stream, baselineSequence[i], 16 );

                            write_bool( stream, cubeState[i].isActive );

                            write_linear_velocity_delta( stream, cubeDelta[i].linearVelocityX, cubeDelta[i].linearVelocityY, cubeDelta[i].linearVelocityZ );

                            write_angular_velocity_delta( stream, cubeDelta[i].angularVelocityX, cubeDelta[i].angularVelocityY, cubeDelta[i].angularVelocityZ );

                            write_position_delta( stream, cubeDelta[i].positionX, cubeDelta[i].positionY, cubeDelta[i].positionZ );

                            write_bits( stream, cubeState[i].rotationLargest, 2 );
                            write_bits( stream, cubeState[i].rotationX, Constants.RotationBits );
                            write_bits( stream, cubeState[i].rotationY, Constants.RotationBits );
                            write_bits( stream, cubeState[i].rotationZ, Constants.RotationBits );
                        }
                        else
                        {
                            write_bool( stream, cubeState[i].isActive );

                            write_int( stream, cubeState[i].positionX, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
                            write_int( stream, cubeState[i].positionY, Constants.PositionMinimumY, Constants.PositionMaximumY );
                            write_int( stream, cubeState[i].positionZ, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );

                            write_bits( stream, cubeState[i].rotationLargest, 2 );
                            write_bits( stream, cubeState[i].rotationX, Constants.RotationBits );
                            write_bits( stream, cubeState[i].rotationY, Constants.RotationBits );
                            write_bits( stream, cubeState[i].rotationZ, Constants.RotationBits );

                            if ( cubeState[i].isActive )
                            {
                                write_int( stream, cubeState[i].linearVelocityX, Constants.LinearVelocityMinimum, Constants.LinearVelocityMaximum );
                                write_int( stream, cubeState[i].linearVelocityY, Constants.LinearVelocityMinimum, Constants.LinearVelocityMaximum );
                                write_int( stream, cubeState[i].linearVelocityZ, Constants.LinearVelocityMinimum, Constants.LinearVelocityMaximum );

                                write_int( stream, cubeState[i].angularVelocityX, Constants.AngularVelocityMinimum, Constants.AngularVelocityMaximum );
                                write_int( stream, cubeState[i].angularVelocityY, Constants.AngularVelocityMinimum, Constants.AngularVelocityMaximum );
                                write_int( stream, cubeState[i].angularVelocityZ, Constants.AngularVelocityMinimum, Constants.AngularVelocityMaximum );
                            }
                        }
                    }
                }
            }
        }
    }

    public void ReadStateUpdatePacketHeader( Network.ReadStream stream, out Network.PacketHeader header )
    {
        byte packetType = 0;

        read_bits( stream, out packetType, 8 );

        Debug.Assert( packetType == (byte) PacketType.StateUpdate );

        read_bits( stream, out header.sequence, 16 );
        read_bits( stream, out header.ack, 16 );
        read_bits( stream, out header.ack_bits, 32 );
        read_bits( stream, out header.frameNumber, 32 );
        read_bits( stream, out header.resetSequence, 16 );
        read_float( stream, out header.avatarSampleTimeOffset );
    }

    public void ReadStateUpdatePacket( Network.ReadStream stream, out Network.PacketHeader header, out int numAvatarStates, AvatarStateQuantized[] avatarState, out int numStateUpdates, int[] cubeIds, bool[] notChanged, bool[] hasDelta, bool[] perfectPrediction, bool[] hasPredictionDelta, ushort[] baselineSequence, CubeState[] cubeState, CubeDelta[] cubeDelta, CubeDelta[] predictionDelta )
    {
        byte packetType = 0;

        read_bits( stream, out packetType, 8 );

        Debug.Assert( packetType == (byte) PacketType.StateUpdate );

        read_bits( stream, out header.sequence, 16 );
        read_bits( stream, out header.ack, 16 );
        read_bits( stream, out header.ack_bits, 32 );
        read_bits( stream, out header.frameNumber, 32 );
        read_bits( stream, out header.resetSequence, 16 );
        read_float( stream, out header.avatarSampleTimeOffset );

        read_int( stream, out numAvatarStates, 0, Constants.MaxClients );
        for ( int i = 0; i < numAvatarStates; ++i )
        {
            read_avatar_state( stream, out avatarState[i] );
        }

        read_int( stream, out numStateUpdates, 0, Constants.MaxStateUpdates );

        for ( int i = 0; i < numStateUpdates; ++i )
        {
            hasDelta[i] = false;
            perfectPrediction[i] = false;
            hasPredictionDelta[i] = false;

            read_int( stream, out cubeIds[i], 0, Constants.NumCubes - 1 );

#if DEBUG_DELTA_COMPRESSION
            read_int( stream, out cubeDelta[i].absolute_position_x, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
            read_int( stream, out cubeDelta[i].absolute_position_y, Constants.PositionMinimumY, Constants.PositionMaximumY );
            read_int( stream, out cubeDelta[i].absolute_position_z, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
#endif // #if DEBUG_DELTA_COMPRESSION

            read_int( stream, out cubeState[i].authorityId, 0, Constants.MaxAuthority - 1 );
            read_bits( stream, out cubeState[i].authoritySequence, 16 );
            read_bits( stream, out cubeState[i].ownershipSequence, 16 );

            read_bool( stream, out notChanged[i] );

            if ( notChanged[i] )
            {
                read_bits( stream, out baselineSequence[i], 16 );
            }
            else
            {
                read_bool( stream, out perfectPrediction[i] );

                if ( perfectPrediction[i] )
                {
                    read_bits( stream, out baselineSequence[i], 16 );

                    read_bits( stream, out cubeState[i].rotationLargest, 2 );
                    read_bits( stream, out cubeState[i].rotationX, Constants.RotationBits );
                    read_bits( stream, out cubeState[i].rotationY, Constants.RotationBits );
                    read_bits( stream, out cubeState[i].rotationZ, Constants.RotationBits );

                    cubeState[i].isActive = true;
                }
                else
                {
                    read_bool( stream, out hasPredictionDelta[i] );

                    if ( hasPredictionDelta[i] )
                    {
                        read_bits( stream, out baselineSequence[i], 16 );

                        read_bool( stream, out cubeState[i].isActive );

                        read_linear_velocity_delta( stream, out predictionDelta[i].linearVelocityX, out predictionDelta[i].linearVelocityY, out predictionDelta[i].linearVelocityZ );

                        read_angular_velocity_delta( stream, out predictionDelta[i].angularVelocityX, out predictionDelta[i].angularVelocityY, out predictionDelta[i].angularVelocityZ );

                        read_position_delta( stream, out predictionDelta[i].positionX, out predictionDelta[i].positionY, out predictionDelta[i].positionZ );

                        read_bits( stream, out cubeState[i].rotationLargest, 2 );
                        read_bits( stream, out cubeState[i].rotationX, Constants.RotationBits );
                        read_bits( stream, out cubeState[i].rotationY, Constants.RotationBits );
                        read_bits( stream, out cubeState[i].rotationZ, Constants.RotationBits );
                    }
                    else
                    {
                        read_bool( stream, out hasDelta[i] );

                        if ( hasDelta[i] )
                        {
                            read_bits( stream, out baselineSequence[i], 16 );

                            read_bool( stream, out cubeState[i].isActive );

                            read_linear_velocity_delta( stream, out cubeDelta[i].linearVelocityX, out cubeDelta[i].linearVelocityY, out cubeDelta[i].linearVelocityZ );

                            read_angular_velocity_delta( stream, out cubeDelta[i].angularVelocityX, out cubeDelta[i].angularVelocityY, out cubeDelta[i].angularVelocityZ );

                            read_position_delta( stream, out cubeDelta[i].positionX, out cubeDelta[i].positionY, out cubeDelta[i].positionZ );

                            read_bits( stream, out cubeState[i].rotationLargest, 2 );
                            read_bits( stream, out cubeState[i].rotationX, Constants.RotationBits );
                            read_bits( stream, out cubeState[i].rotationY, Constants.RotationBits );
                            read_bits( stream, out cubeState[i].rotationZ, Constants.RotationBits );
                        }
                        else
                        {
                            read_bool( stream, out cubeState[i].isActive );

                            read_int( stream, out cubeState[i].positionX, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
                            read_int( stream, out cubeState[i].positionY, Constants.PositionMinimumY, Constants.PositionMaximumY );
                            read_int( stream, out cubeState[i].positionZ, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );

                            read_bits( stream, out cubeState[i].rotationLargest, 2 );
                            read_bits( stream, out cubeState[i].rotationX, Constants.RotationBits );
                            read_bits( stream, out cubeState[i].rotationY, Constants.RotationBits );
                            read_bits( stream, out cubeState[i].rotationZ, Constants.RotationBits );

                            if ( cubeState[i].isActive )
                            {
                                read_int( stream, out cubeState[i].linearVelocityX, Constants.LinearVelocityMinimum, Constants.LinearVelocityMaximum );
                                read_int( stream, out cubeState[i].linearVelocityY, Constants.LinearVelocityMinimum, Constants.LinearVelocityMaximum );
                                read_int( stream, out cubeState[i].linearVelocityZ, Constants.LinearVelocityMinimum, Constants.LinearVelocityMaximum );

                                read_int( stream, out cubeState[i].angularVelocityX, Constants.AngularVelocityMinimum, Constants.AngularVelocityMaximum );
                                read_int( stream, out cubeState[i].angularVelocityY, Constants.AngularVelocityMinimum, Constants.AngularVelocityMaximum );
                                read_int( stream, out cubeState[i].angularVelocityZ, Constants.AngularVelocityMinimum, Constants.AngularVelocityMaximum );
                            }
                            else
                            {
                                cubeState[i].linearVelocityX = 0;
                                cubeState[i].linearVelocityY = 0;
                                cubeState[i].linearVelocityZ = 0;

                                cubeState[i].angularVelocityX = 0;
                                cubeState[i].angularVelocityY = 0;
                                cubeState[i].angularVelocityZ = 0;
                            }
                        }
                    }
                }
            }
        }
    }

    void write_position_delta( Network.WriteStream stream, int delta_x, int delta_y, int delta_z )
    {
        Assert.IsTrue( delta_x >= -Constants.PositionDeltaMax );
        Assert.IsTrue( delta_x <= +Constants.PositionDeltaMax );
        Assert.IsTrue( delta_y >= -Constants.PositionDeltaMax );
        Assert.IsTrue( delta_y <= +Constants.PositionDeltaMax );
        Assert.IsTrue( delta_z >= -Constants.PositionDeltaMax );
        Assert.IsTrue( delta_z <= +Constants.PositionDeltaMax );

        uint unsigned_x = Network.Util.SignedToUnsigned( delta_x );
        uint unsigned_y = Network.Util.SignedToUnsigned( delta_y );
        uint unsigned_z = Network.Util.SignedToUnsigned( delta_z );

        bool small_x = unsigned_x <= Constants.PositionDeltaSmallThreshold;
        bool small_y = unsigned_y <= Constants.PositionDeltaSmallThreshold;
        bool small_z = unsigned_z <= Constants.PositionDeltaSmallThreshold;

        bool all_small = small_x && small_y && small_z;

        write_bool( stream, all_small );

        if ( all_small )
        {
            write_bits( stream, unsigned_x, Constants.PositionDeltaSmallBits );
            write_bits( stream, unsigned_y, Constants.PositionDeltaSmallBits );
            write_bits( stream, unsigned_z, Constants.PositionDeltaSmallBits );
        }
        else
        {
            write_bool( stream, small_x );

            if ( small_x )
            {
                write_bits( stream, unsigned_x, Constants.PositionDeltaSmallBits );
            }
            else
            {
                unsigned_x -= Constants.PositionDeltaSmallThreshold;

                bool medium_x = unsigned_x < Constants.PositionDeltaMediumThreshold;

                write_bool( stream, medium_x );

                if ( medium_x )
                {
                    write_bits( stream, unsigned_x, Constants.PositionDeltaMediumBits );
                }
                else
                {
                    write_int( stream, delta_x, -Constants.PositionDeltaMax, +Constants.PositionDeltaMax );
                }
            }

            write_bool( stream, small_y );

            if ( small_y )
            {
                write_bits( stream, unsigned_y, Constants.PositionDeltaSmallBits );
            }
            else
            {
                unsigned_y -= Constants.PositionDeltaSmallThreshold;

                bool medium_y = unsigned_y < Constants.PositionDeltaMediumThreshold;

                write_bool( stream, medium_y );

                if ( medium_y )
                {
                    write_bits( stream, unsigned_y, Constants.PositionDeltaMediumBits );
                }
                else
                {
                    write_int( stream, delta_y, -Constants.PositionDeltaMax, +Constants.PositionDeltaMax );
                }
            }

            write_bool( stream, small_z );

            if ( small_z )
            {
                write_bits( stream, unsigned_z, Constants.PositionDeltaSmallBits );
            }
            else
            {
                unsigned_z -= Constants.PositionDeltaSmallThreshold;

                bool medium_z = unsigned_z < Constants.PositionDeltaMediumThreshold;

                write_bool( stream, medium_z );

                if ( medium_z )
                {
                    write_bits( stream, unsigned_z, Constants.PositionDeltaMediumBits );
                }
                else
                {
                    write_int( stream, delta_z, -Constants.PositionDeltaMax, +Constants.PositionDeltaMax );
                }
            }
        }
    }

    void read_position_delta( Network.ReadStream stream, out int delta_x, out int delta_y, out int delta_z )
    {
        bool all_small;

        read_bool( stream, out all_small );

        uint unsigned_x;
        uint unsigned_y;
        uint unsigned_z;

        if ( all_small )
        {
            read_bits( stream, out unsigned_x, Constants.PositionDeltaSmallBits );
            read_bits( stream, out unsigned_y, Constants.PositionDeltaSmallBits );
            read_bits( stream, out unsigned_z, Constants.PositionDeltaSmallBits );

            delta_x = Network.Util.UnsignedToSigned( unsigned_x );
            delta_y = Network.Util.UnsignedToSigned( unsigned_y );
            delta_z = Network.Util.UnsignedToSigned( unsigned_z );
        }
        else
        {
            bool small_x;

            read_bool( stream, out small_x );

            if ( small_x )
            {
                read_bits( stream, out unsigned_x, Constants.PositionDeltaSmallBits );

                delta_x = Network.Util.UnsignedToSigned( unsigned_x );
            }
            else
            {
                bool medium_x;

                read_bool( stream, out medium_x );

                if ( medium_x )
                {
                    read_bits( stream, out unsigned_x, Constants.PositionDeltaMediumBits );

                    delta_x = Network.Util.UnsignedToSigned( unsigned_x + Constants.PositionDeltaSmallThreshold );
                }
                else
                {
                    read_int( stream, out delta_x, -Constants.PositionDeltaMax, +Constants.PositionDeltaMax );
                }
            }

            bool small_y;

            read_bool( stream, out small_y );

            if ( small_y )
            {
                read_bits( stream, out unsigned_y, Constants.PositionDeltaSmallBits );

                delta_y = Network.Util.UnsignedToSigned( unsigned_y );
            }
            else
            {
                bool medium_y;

                read_bool( stream, out medium_y );

                if ( medium_y )
                {
                    read_bits( stream, out unsigned_y, Constants.PositionDeltaMediumBits );

                    delta_y = Network.Util.UnsignedToSigned( unsigned_y + Constants.PositionDeltaSmallThreshold );
                }
                else
                {
                    read_int( stream, out delta_y, -Constants.PositionDeltaMax, +Constants.PositionDeltaMax );
                }
            }

            bool small_z;

            read_bool( stream, out small_z );

            if ( small_z )
            {
                read_bits( stream, out unsigned_z, Constants.PositionDeltaSmallBits );

                delta_z = Network.Util.UnsignedToSigned( unsigned_z );
            }
            else
            {
                bool medium_z;

                read_bool( stream, out medium_z );

                if ( medium_z )
                {
                    read_bits( stream, out unsigned_z, Constants.PositionDeltaMediumBits );

                    delta_z = Network.Util.UnsignedToSigned( unsigned_z + Constants.PositionDeltaSmallThreshold );
                }
                else
                {
                    read_int( stream, out delta_z, -Constants.PositionDeltaMax, +Constants.PositionDeltaMax );
                }
            }
        }
    }

    void write_linear_velocity_delta( Network.WriteStream stream, int delta_x, int delta_y, int delta_z )
    {
        Assert.IsTrue( delta_x >= -Constants.LinearVelocityDeltaMax );
        Assert.IsTrue( delta_x <= +Constants.LinearVelocityDeltaMax );
        Assert.IsTrue( delta_y >= -Constants.LinearVelocityDeltaMax );
        Assert.IsTrue( delta_y <= +Constants.LinearVelocityDeltaMax );
        Assert.IsTrue( delta_z >= -Constants.LinearVelocityDeltaMax );
        Assert.IsTrue( delta_z <= +Constants.LinearVelocityDeltaMax );

        uint unsigned_x = Network.Util.SignedToUnsigned( delta_x );
        uint unsigned_y = Network.Util.SignedToUnsigned( delta_y );
        uint unsigned_z = Network.Util.SignedToUnsigned( delta_z );

        bool small_x = unsigned_x <= Constants.LinearVelocityDeltaSmallThreshold;
        bool small_y = unsigned_y <= Constants.LinearVelocityDeltaSmallThreshold;
        bool small_z = unsigned_z <= Constants.LinearVelocityDeltaSmallThreshold;

        bool all_small = small_x && small_y && small_z;

        write_bool( stream, all_small );

        if ( all_small )
        {
            write_bits( stream, unsigned_x, Constants.LinearVelocityDeltaSmallBits );
            write_bits( stream, unsigned_y, Constants.LinearVelocityDeltaSmallBits );
            write_bits( stream, unsigned_z, Constants.LinearVelocityDeltaSmallBits );
        }
        else
        {
            write_bool( stream, small_x );

            if ( small_x )
            {
                write_bits( stream, unsigned_x, Constants.LinearVelocityDeltaSmallBits );
            }
            else
            {
                unsigned_x -= Constants.LinearVelocityDeltaSmallThreshold;

                bool medium_x = unsigned_x < Constants.LinearVelocityDeltaMediumThreshold;

                write_bool( stream, medium_x );

                if ( medium_x )
                {
                    write_bits( stream, unsigned_x, Constants.LinearVelocityDeltaMediumBits );
                }
                else
                {
                    write_int( stream, delta_x, -Constants.LinearVelocityDeltaMax, +Constants.LinearVelocityDeltaMax );
                }
            }

            write_bool( stream, small_y );

            if ( small_y )
            {
                write_bits( stream, unsigned_y, Constants.LinearVelocityDeltaSmallBits );
            }
            else
            {
                unsigned_y -= Constants.LinearVelocityDeltaSmallThreshold;

                bool medium_y = unsigned_y < Constants.LinearVelocityDeltaMediumThreshold;

                write_bool( stream, medium_y );

                if ( medium_y )
                {
                    write_bits( stream, unsigned_y, Constants.LinearVelocityDeltaMediumBits );
                }
                else
                {
                    write_int( stream, delta_y, -Constants.LinearVelocityDeltaMax, +Constants.LinearVelocityDeltaMax );
                }
            }

            write_bool( stream, small_z );

            if ( small_z )
            {
                write_bits( stream, unsigned_z, Constants.LinearVelocityDeltaSmallBits );
            }
            else
            {
                unsigned_z -= Constants.LinearVelocityDeltaSmallThreshold;

                bool medium_z = unsigned_z < Constants.LinearVelocityDeltaMediumThreshold;

                write_bool( stream, medium_z );

                if ( medium_z )
                {
                    write_bits( stream, unsigned_z, Constants.LinearVelocityDeltaMediumBits );
                }
                else
                {
                    write_int( stream, delta_z, -Constants.LinearVelocityDeltaMax, +Constants.LinearVelocityDeltaMax );
                }
            }
        }
    }

    void read_linear_velocity_delta( Network.ReadStream stream, out int delta_x, out int delta_y, out int delta_z )
    {
        bool all_small;

        read_bool( stream, out all_small );

        uint unsigned_x;
        uint unsigned_y;
        uint unsigned_z;

        if ( all_small )
        {
            read_bits( stream, out unsigned_x, Constants.LinearVelocityDeltaSmallBits );
            read_bits( stream, out unsigned_y, Constants.LinearVelocityDeltaSmallBits );
            read_bits( stream, out unsigned_z, Constants.LinearVelocityDeltaSmallBits );

            delta_x = Network.Util.UnsignedToSigned( unsigned_x );
            delta_y = Network.Util.UnsignedToSigned( unsigned_y );
            delta_z = Network.Util.UnsignedToSigned( unsigned_z );
        }
        else
        {
            bool small_x;

            read_bool( stream, out small_x );

            if ( small_x )
            {
                read_bits( stream, out unsigned_x, Constants.LinearVelocityDeltaSmallBits );

                delta_x = Network.Util.UnsignedToSigned( unsigned_x );
            }
            else
            {
                bool medium_x;

                read_bool( stream, out medium_x );

                if ( medium_x )
                {
                    read_bits( stream, out unsigned_x, Constants.LinearVelocityDeltaMediumBits );

                    delta_x = Network.Util.UnsignedToSigned( unsigned_x + Constants.LinearVelocityDeltaSmallThreshold );
                }
                else
                {
                    read_int( stream, out delta_x, -Constants.LinearVelocityDeltaMax, +Constants.LinearVelocityDeltaMax );
                }
            }

            bool small_y;

            read_bool( stream, out small_y );

            if ( small_y )
            {
                read_bits( stream, out unsigned_y, Constants.LinearVelocityDeltaSmallBits );

                delta_y = Network.Util.UnsignedToSigned( unsigned_y );
            }
            else
            {
                bool medium_y;

                read_bool( stream, out medium_y );

                if ( medium_y )
                {
                    read_bits( stream, out unsigned_y, Constants.LinearVelocityDeltaMediumBits );

                    delta_y = Network.Util.UnsignedToSigned( unsigned_y + Constants.LinearVelocityDeltaSmallThreshold );
                }
                else
                {
                    read_int( stream, out delta_y, -Constants.LinearVelocityDeltaMax, +Constants.LinearVelocityDeltaMax );
                }
            }

            bool small_z;

            read_bool( stream, out small_z );

            if ( small_z )
            {
                read_bits( stream, out unsigned_z, Constants.LinearVelocityDeltaSmallBits );

                delta_z = Network.Util.UnsignedToSigned( unsigned_z );
            }
            else
            {
                bool medium_z;

                read_bool( stream, out medium_z );

                if ( medium_z )
                {
                    read_bits( stream, out unsigned_z, Constants.LinearVelocityDeltaMediumBits );

                    delta_z = Network.Util.UnsignedToSigned( unsigned_z + Constants.LinearVelocityDeltaSmallThreshold );
                }
                else
                {
                    read_int( stream, out delta_z, -Constants.LinearVelocityDeltaMax, +Constants.LinearVelocityDeltaMax );
                }
            }
        }
    }

    void write_angular_velocity_delta( Network.WriteStream stream, int delta_x, int delta_y, int delta_z )
    {
        Assert.IsTrue( delta_x >= -Constants.AngularVelocityDeltaMax );
        Assert.IsTrue( delta_x <= +Constants.AngularVelocityDeltaMax );
        Assert.IsTrue( delta_y >= -Constants.AngularVelocityDeltaMax );
        Assert.IsTrue( delta_y <= +Constants.AngularVelocityDeltaMax );
        Assert.IsTrue( delta_z >= -Constants.AngularVelocityDeltaMax );
        Assert.IsTrue( delta_z <= +Constants.AngularVelocityDeltaMax );

        uint unsigned_x = Network.Util.SignedToUnsigned( delta_x );
        uint unsigned_y = Network.Util.SignedToUnsigned( delta_y );
        uint unsigned_z = Network.Util.SignedToUnsigned( delta_z );

        bool small_x = unsigned_x <= Constants.AngularVelocityDeltaSmallThreshold;
        bool small_y = unsigned_y <= Constants.AngularVelocityDeltaSmallThreshold;
        bool small_z = unsigned_z <= Constants.AngularVelocityDeltaSmallThreshold;

        bool all_small = small_x && small_y && small_z;

        write_bool( stream, all_small );

        if ( all_small )
        {
            write_bits( stream, unsigned_x, Constants.AngularVelocityDeltaSmallBits );
            write_bits( stream, unsigned_y, Constants.AngularVelocityDeltaSmallBits );
            write_bits( stream, unsigned_z, Constants.AngularVelocityDeltaSmallBits );
        }
        else
        {
            write_bool( stream, small_x );

            if ( small_x )
            {
                write_bits( stream, unsigned_x, Constants.AngularVelocityDeltaSmallBits );
            }
            else
            {
                unsigned_x -= Constants.AngularVelocityDeltaSmallThreshold;

                bool medium_x = unsigned_x < Constants.AngularVelocityDeltaMediumThreshold;

                write_bool( stream, medium_x );

                if ( medium_x )
                {
                    write_bits( stream, unsigned_x, Constants.AngularVelocityDeltaMediumBits );
                }
                else
                {
                    write_int( stream, delta_x, -Constants.AngularVelocityDeltaMax, +Constants.AngularVelocityDeltaMax );
                }
            }

            write_bool( stream, small_y );

            if ( small_y )
            {
                write_bits( stream, unsigned_y, Constants.AngularVelocityDeltaSmallBits );
            }
            else
            {
                unsigned_y -= Constants.AngularVelocityDeltaSmallThreshold;

                bool medium_y = unsigned_y < Constants.AngularVelocityDeltaMediumThreshold;

                write_bool( stream, medium_y );

                if ( medium_y )
                {
                    write_bits( stream, unsigned_y, Constants.AngularVelocityDeltaMediumBits );
                }
                else
                {
                    write_int( stream, delta_y, -Constants.AngularVelocityDeltaMax, +Constants.AngularVelocityDeltaMax );
                }
            }

            write_bool( stream, small_z );

            if ( small_z )
            {
                write_bits( stream, unsigned_z, Constants.AngularVelocityDeltaSmallBits );
            }
            else
            {
                unsigned_z -= Constants.AngularVelocityDeltaSmallThreshold;

                bool medium_z = unsigned_z < Constants.AngularVelocityDeltaMediumThreshold;

                write_bool( stream, medium_z );

                if ( medium_z )
                {
                    write_bits( stream, unsigned_z, Constants.AngularVelocityDeltaMediumBits );
                }
                else
                {
                    write_int( stream, delta_z, -Constants.AngularVelocityDeltaMax, +Constants.AngularVelocityDeltaMax );
                }
            }
        }
    }

    void read_angular_velocity_delta( Network.ReadStream stream, out int delta_x, out int delta_y, out int delta_z )
    {
        bool all_small;

        read_bool( stream, out all_small );

        uint unsigned_x;
        uint unsigned_y;
        uint unsigned_z;

        if ( all_small )
        {
            read_bits( stream, out unsigned_x, Constants.AngularVelocityDeltaSmallBits );
            read_bits( stream, out unsigned_y, Constants.AngularVelocityDeltaSmallBits );
            read_bits( stream, out unsigned_z, Constants.AngularVelocityDeltaSmallBits );

            delta_x = Network.Util.UnsignedToSigned( unsigned_x );
            delta_y = Network.Util.UnsignedToSigned( unsigned_y );
            delta_z = Network.Util.UnsignedToSigned( unsigned_z );
        }
        else
        {
            bool small_x;

            read_bool( stream, out small_x );

            if ( small_x )
            {
                read_bits( stream, out unsigned_x, Constants.AngularVelocityDeltaSmallBits );

                delta_x = Network.Util.UnsignedToSigned( unsigned_x );
            }
            else
            {
                bool medium_x;

                read_bool( stream, out medium_x );

                if ( medium_x )
                {
                    read_bits( stream, out unsigned_x, Constants.AngularVelocityDeltaMediumBits );

                    delta_x = Network.Util.UnsignedToSigned( unsigned_x + Constants.AngularVelocityDeltaSmallThreshold );
                }
                else
                {
                    read_int( stream, out delta_x, -Constants.AngularVelocityDeltaMax, +Constants.AngularVelocityDeltaMax );
                }
            }

            bool small_y;

            read_bool( stream, out small_y );

            if ( small_y )
            {
                read_bits( stream, out unsigned_y, Constants.AngularVelocityDeltaSmallBits );

                delta_y = Network.Util.UnsignedToSigned( unsigned_y );
            }
            else
            {
                bool medium_y;

                read_bool( stream, out medium_y );

                if ( medium_y )
                {
                    read_bits( stream, out unsigned_y, Constants.AngularVelocityDeltaMediumBits );

                    delta_y = Network.Util.UnsignedToSigned( unsigned_y + Constants.AngularVelocityDeltaSmallThreshold );
                }
                else
                {
                    read_int( stream, out delta_y, -Constants.AngularVelocityDeltaMax, +Constants.AngularVelocityDeltaMax );
                }
            }

            bool small_z;

            read_bool( stream, out small_z );

            if ( small_z )
            {
                read_bits( stream, out unsigned_z, Constants.AngularVelocityDeltaSmallBits );

                delta_z = Network.Util.UnsignedToSigned( unsigned_z );
            }
            else
            {
                bool medium_z;

                read_bool( stream, out medium_z );

                if ( medium_z )
                {
                    read_bits( stream, out unsigned_z, Constants.AngularVelocityDeltaMediumBits );

                    delta_z = Network.Util.UnsignedToSigned( unsigned_z + Constants.AngularVelocityDeltaSmallThreshold );
                }
                else
                {
                    read_int( stream, out delta_z, -Constants.AngularVelocityDeltaMax, +Constants.AngularVelocityDeltaMax );
                }
            }
        }
    }

    void write_avatar_state( Network.WriteStream stream, ref AvatarStateQuantized avatarState )
    {
        write_int( stream, avatarState.clientId, 0, Constants.MaxClients - 1 );

        write_int( stream, avatarState.headPositionX, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
        write_int( stream, avatarState.headPositionY, Constants.PositionMinimumY, Constants.PositionMaximumY );
        write_int( stream, avatarState.headPositionZ, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );

        write_bits( stream, avatarState.headRotationLargest, 2 );
        write_bits( stream, avatarState.headRotationX, Constants.RotationBits );
        write_bits( stream, avatarState.headRotationY, Constants.RotationBits );
        write_bits( stream, avatarState.headRotationZ, Constants.RotationBits );

        write_int( stream, avatarState.leftHandPositionX, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
        write_int( stream, avatarState.leftHandPositionY, Constants.PositionMinimumY, Constants.PositionMaximumY );
        write_int( stream, avatarState.leftHandPositionZ, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );

        write_bits( stream, avatarState.leftHandRotationLargest, 2 );
        write_bits( stream, avatarState.leftHandRotationX, Constants.RotationBits );
        write_bits( stream, avatarState.leftHandRotationY, Constants.RotationBits );
        write_bits( stream, avatarState.leftHandRotationZ, Constants.RotationBits );

        write_int( stream, avatarState.leftHandGripTrigger, Constants.TriggerMinimum, Constants.TriggerMaximum );
        write_int( stream, avatarState.leftHandIdTrigger, Constants.TriggerMinimum, Constants.TriggerMaximum );
        write_bool( stream, avatarState.isLeftHandPointing );
        write_bool( stream, avatarState.areLeftHandThumbsUp );

        write_bool( stream, avatarState.isLeftHandHoldingCube );

        if ( avatarState.isLeftHandHoldingCube )
        {
            write_int( stream, avatarState.leftHandCubeId, 0, Constants.NumCubes - 1 );
            write_bits( stream, avatarState.leftHandAuthoritySequence, 16 );
            write_bits( stream, avatarState.leftHandOwnershipSequence, 16 );

            write_int( stream, avatarState.leftHandCubeLocalPositionX, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );
            write_int( stream, avatarState.leftHandCubeLocalPositionY, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );
            write_int( stream, avatarState.leftHandCubeLocalPositionZ, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );

            write_bits( stream, avatarState.leftHandCubeLocalRotationLargest, 2 );
            write_bits( stream, avatarState.leftHandCubeLocalRotationX, Constants.RotationBits );
            write_bits( stream, avatarState.leftHandCubeLocalRotationY, Constants.RotationBits );
            write_bits( stream, avatarState.leftHandCubeLocalRotationZ, Constants.RotationBits );
        }

        write_int( stream, avatarState.rightHandPositionX, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
        write_int( stream, avatarState.rightHandPositionY, Constants.PositionMinimumY, Constants.PositionMaximumY );
        write_int( stream, avatarState.rightHandPositionZ, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );

        write_bits( stream, avatarState.rightHandRotationLargest, 2 );
        write_bits( stream, avatarState.rightHandRotationX, Constants.RotationBits );
        write_bits( stream, avatarState.rightHandRotationY, Constants.RotationBits );
        write_bits( stream, avatarState.rightHandRotationZ, Constants.RotationBits );

        write_int( stream, avatarState.rightHandGripTrigger, Constants.TriggerMinimum, Constants.TriggerMaximum );
        write_int( stream, avatarState.rightHandIndexTrigger, Constants.TriggerMinimum, Constants.TriggerMaximum );
        write_bool( stream, avatarState.isRightHandPointing );
        write_bool( stream, avatarState.areRightHandThumbsUp );

        write_bool( stream, avatarState.isRightHandHoldingCube );

        if ( avatarState.isRightHandHoldingCube )
        {
            write_int( stream, avatarState.rightHandCubeId, 0, Constants.NumCubes - 1 );
            write_bits( stream, avatarState.rightHandAuthoritySequence, 16 );
            write_bits( stream, avatarState.rightHandOwnershipSequence, 16 );

            write_int( stream, avatarState.rightHandCubeLocalPositionX, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );
            write_int( stream, avatarState.rightHandCubeLocalPositionY, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );
            write_int( stream, avatarState.rightHandCubeLocalPositionZ, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );

            write_bits( stream, avatarState.rightHandCubeLocalRotationLargest, 2 );
            write_bits( stream, avatarState.rightHandCubeLocalRotationX, Constants.RotationBits );
            write_bits( stream, avatarState.rightHandCubeLocalRotationY, Constants.RotationBits );
            write_bits( stream, avatarState.rightHandCubeLocalRotationZ, Constants.RotationBits );
        }

        write_int( stream, avatarState.voiceAmplitude, Constants.VoiceMinimum, Constants.VoiceMaximum );
    }

    void read_avatar_state( Network.ReadStream stream, out AvatarStateQuantized avatarState )
    {
        read_int( stream, out avatarState.clientId, 0, Constants.MaxClients - 1 );

        read_int( stream, out avatarState.headPositionX, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
        read_int( stream, out avatarState.headPositionY, Constants.PositionMinimumY, Constants.PositionMaximumY );
        read_int( stream, out avatarState.headPositionZ, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );

        read_bits( stream, out avatarState.headRotationLargest, 2 );
        read_bits( stream, out avatarState.headRotationX, Constants.RotationBits );
        read_bits( stream, out avatarState.headRotationY, Constants.RotationBits );
        read_bits( stream, out avatarState.headRotationZ, Constants.RotationBits );

        read_int( stream, out avatarState.leftHandPositionX, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
        read_int( stream, out avatarState.leftHandPositionY, Constants.PositionMinimumY, Constants.PositionMaximumY );
        read_int( stream, out avatarState.leftHandPositionZ, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );

        read_bits( stream, out avatarState.leftHandRotationLargest, 2 );
        read_bits( stream, out avatarState.leftHandRotationX, Constants.RotationBits );
        read_bits( stream, out avatarState.leftHandRotationY, Constants.RotationBits );
        read_bits( stream, out avatarState.leftHandRotationZ, Constants.RotationBits );

        read_int( stream, out avatarState.leftHandGripTrigger, Constants.TriggerMinimum, Constants.TriggerMaximum );
        read_int( stream, out avatarState.leftHandIdTrigger, Constants.TriggerMinimum, Constants.TriggerMaximum );
        read_bool( stream, out avatarState.isLeftHandPointing );
        read_bool( stream, out avatarState.areLeftHandThumbsUp );

        read_bool( stream, out avatarState.isLeftHandHoldingCube );

        if ( avatarState.isLeftHandHoldingCube )
        {
            read_int( stream, out avatarState.leftHandCubeId, 0, Constants.NumCubes - 1 );
            read_bits( stream, out avatarState.leftHandAuthoritySequence, 16 );
            read_bits( stream, out avatarState.leftHandOwnershipSequence, 16 );

            read_int( stream, out avatarState.leftHandCubeLocalPositionX, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );
            read_int( stream, out avatarState.leftHandCubeLocalPositionY, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );
            read_int( stream, out avatarState.leftHandCubeLocalPositionZ, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );

            read_bits( stream, out avatarState.leftHandCubeLocalRotationLargest, 2 );
            read_bits( stream, out avatarState.leftHandCubeLocalRotationX, Constants.RotationBits );
            read_bits( stream, out avatarState.leftHandCubeLocalRotationY, Constants.RotationBits );
            read_bits( stream, out avatarState.leftHandCubeLocalRotationZ, Constants.RotationBits );
        }
        else
        {
            avatarState.leftHandCubeId = 0;
            avatarState.leftHandAuthoritySequence = 0;
            avatarState.leftHandOwnershipSequence = 0;
            avatarState.leftHandCubeLocalPositionX = 0;
            avatarState.leftHandCubeLocalPositionY = 0;
            avatarState.leftHandCubeLocalPositionZ = 0;
            avatarState.leftHandCubeLocalRotationLargest = 0;
            avatarState.leftHandCubeLocalRotationX = 0;
            avatarState.leftHandCubeLocalRotationY = 0;
            avatarState.leftHandCubeLocalRotationZ = 0;
        }

        read_int( stream, out avatarState.rightHandPositionX, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );
        read_int( stream, out avatarState.rightHandPositionY, Constants.PositionMinimumY, Constants.PositionMaximumY );
        read_int( stream, out avatarState.rightHandPositionZ, Constants.PositionMinimumXZ, Constants.PositionMaximumXZ );

        read_bits( stream, out avatarState.rightHandRotationLargest, 2 );
        read_bits( stream, out avatarState.rightHandRotationX, Constants.RotationBits );
        read_bits( stream, out avatarState.rightHandRotationY, Constants.RotationBits );
        read_bits( stream, out avatarState.rightHandRotationZ, Constants.RotationBits );

        read_int( stream, out avatarState.rightHandGripTrigger, Constants.TriggerMinimum, Constants.TriggerMaximum );
        read_int( stream, out avatarState.rightHandIndexTrigger, Constants.TriggerMinimum, Constants.TriggerMaximum );
        read_bool( stream, out avatarState.isRightHandPointing );
        read_bool( stream, out avatarState.areRightHandThumbsUp );

        read_bool( stream, out avatarState.isRightHandHoldingCube );

        if ( avatarState.isRightHandHoldingCube )
        {
            read_int( stream, out avatarState.rightHandCubeId, 0, Constants.NumCubes - 1 );
            read_bits( stream, out avatarState.rightHandAuthoritySequence, 16 );
            read_bits( stream, out avatarState.rightHandOwnershipSequence, 16 );

            read_int( stream, out avatarState.rightHandCubeLocalPositionX, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );
            read_int( stream, out avatarState.rightHandCubeLocalPositionY, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );
            read_int( stream, out avatarState.rightHandCubeLocalPositionZ, Constants.LocalPositionMinimum, Constants.LocalPositionMaximum );

            read_bits( stream, out avatarState.rightHandCubeLocalRotationLargest, 2 );
            read_bits( stream, out avatarState.rightHandCubeLocalRotationX, Constants.RotationBits );
            read_bits( stream, out avatarState.rightHandCubeLocalRotationY, Constants.RotationBits );
            read_bits( stream, out avatarState.rightHandCubeLocalRotationZ, Constants.RotationBits );
        }
        else
        {
            avatarState.rightHandCubeId = 0;
            avatarState.rightHandAuthoritySequence = 0;
            avatarState.rightHandOwnershipSequence = 0;
            avatarState.rightHandCubeLocalPositionX = 0;
            avatarState.rightHandCubeLocalPositionY = 0;
            avatarState.rightHandCubeLocalPositionZ = 0;
            avatarState.rightHandCubeLocalRotationLargest = 0;
            avatarState.rightHandCubeLocalRotationX = 0;
            avatarState.rightHandCubeLocalRotationY = 0;
            avatarState.rightHandCubeLocalRotationZ = 0;
        }

        read_int( stream, out avatarState.voiceAmplitude, Constants.VoiceMinimum, Constants.VoiceMaximum );
    }
}
