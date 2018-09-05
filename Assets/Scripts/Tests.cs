/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using UnityEngine;
using Network;
using static UnityEngine.Assertions.Assert;
using static Network.Util;
using static UnityEngine.Debug;

public static class Tests {
  static void test_bitpacker() {
    Log("test_bitpacker");
    const int BufferSize = 256;
    var buffer = new uint[BufferSize];
    var writer = new Network.BitWriter();
    writer.Start(buffer);
    IsTrue(writer.GetTotalBytes() == BufferSize);
    IsTrue(writer.GetBitsWritten() == 0);
    IsTrue(writer.GetBytesWritten() == 0);
    IsTrue(writer.GetBitsAvailable() == BufferSize * 8);

    writer.Bits(0, 1);
    writer.Bits(1, 1);
    writer.Bits(10, 8);
    writer.Bits(255, 8);
    writer.Bits(1000, 10);
    writer.Bits(50000, 16);
    writer.Bits(9999999, 32);
    writer.Finish();
    const int bitsWritten = 1 + 1 + 8 + 8 + 10 + 16 + 32;
    IsTrue(writer.GetBytesWritten() == 10);
    IsTrue(writer.GetTotalBytes() == BufferSize);
    IsTrue(writer.GetBitsWritten() == bitsWritten);
    IsTrue(writer.GetBitsAvailable() == BufferSize * 8 - bitsWritten);

    int bytesWritten = writer.GetBytesWritten();
    const int ExpectedBytesWritten = 10;
    IsTrue(bytesWritten == ExpectedBytesWritten);

    var readBuffer = writer.GetData();
    IsTrue(readBuffer.Length == ExpectedBytesWritten);

    var reader = new BitReader();
    reader.Start(readBuffer);
    IsTrue(reader.GetBitsRead() == 0);
    IsTrue(reader.GetBitsRemaining() == bytesWritten * 8);

    var a = reader.Bits(1);
    var b = reader.Bits(1);
    var c = reader.Bits(8);
    var d = reader.Bits(8);
    var e = reader.Bits(10);
    var f = reader.Bits(16);
    var g = reader.Bits(32);
    reader.Finish();
    IsTrue(a == 0);
    IsTrue(b == 1);
    IsTrue(c == 10);
    IsTrue(d == 255);
    IsTrue(e == 1000);
    IsTrue(f == 50000);
    IsTrue(g == 9999999);
    IsTrue(reader.GetBitsRead() == bitsWritten);
    IsTrue(reader.GetBitsRemaining() == bytesWritten * 8 - bitsWritten);
  }

  struct TestStruct {
    public bool bool_value;
    public int int_value;
    public uint uint_value;
    public uint bits_value;
  };

  class TestSerializer {
    public void WriteTestStruct(WriteStream stream, ref TestStruct testStruct) {
      stream.Bool(testStruct.bool_value);
      stream.Int(testStruct.int_value, -100, 100);
      stream.Uint(testStruct.uint_value, 100, 1000);
      stream.Bits(testStruct.bits_value, 23);
    }

    public void ReadTestStruct(ReadStream stream, out TestStruct testStruct) {
      stream.Bool(out testStruct.bool_value);
      stream.Int(out testStruct.int_value, -100, 100);
      stream.Uint(out testStruct.uint_value, 100, 1000);
      stream.Bits(out testStruct.bits_value, 23);
    }
  }

  static void test_serialization() {
    Log("test_serialization");
    const int MaxPacketSize = 1024;
    var serializer = new TestSerializer();
    var buffer = new uint[MaxPacketSize / 4];
    var writeStream = new WriteStream();

    writeStream.Start(buffer);
    TestStruct input;
    input.bool_value = true;
    input.int_value = -5;
    input.uint_value = 215;
    input.bits_value = 12345;
    serializer.WriteTestStruct(writeStream, ref input);
    writeStream.Finish();

    var packet = writeStream.GetData();
    var readStream = new ReadStream();
    readStream.Start(packet);
    TestStruct output;
    serializer.ReadTestStruct(readStream, out output);
    readStream.Finish();

    AreEqual(input.bool_value, output.bool_value);
    AreEqual(input.int_value, output.int_value);
    AreEqual(input.uint_value, output.uint_value);
    AreEqual(input.bits_value, output.bits_value);
  }

  struct TestPacketData {
    public ushort sequence;
  };

  static void test_sequence_buffer() {
    Log("test_sequence_buffer");
    const int Size = 256;
    var buffer = new SequenceBuffer<TestPacketData>(Size);

    for (int i = 0; i < Size; ++i) {
      TestPacketData entry;
      entry.sequence = 0;
      IsTrue(buffer.Exists((ushort)i) == false);
      IsTrue(buffer.Available((ushort)i) == true);
      IsTrue(buffer.Find((ushort)i) == -1);
    }

    for (int i = 0; i <= Size * 4; ++i) {
      int index = buffer.Insert((ushort)i);
      IsTrue(index != -1);
      IsTrue(buffer.GetSequence() == i + 1);
      buffer.entries[index].sequence = (ushort)i;
    }

    for (int i = 0; i <= Size; ++i) {      
      int index = buffer.Insert((ushort)i); //note: outside bounds!
      IsTrue(index == -1);
    }

    ushort sequence = Size * 4;
    for (int i = 0; i < Size; ++i) {
      int index = buffer.Find(sequence);
      IsTrue(index >= 0);
      IsTrue(index < Size);
      IsTrue(buffer.entries[index].sequence == sequence);
      sequence--;
    }

    buffer.Reset();
    IsTrue(buffer.GetSequence() == 0);

    for (int i = 0; i < Size; ++i) {
      IsTrue(buffer.Exists((ushort)i) == false);
      IsTrue(buffer.Available((ushort)i) == true);
      IsTrue(buffer.Find((ushort)i) == -1);
    }
  }

  struct TestPacketData32 {
    public uint sequence;
  };

  static void test_sequence_buffer32() {
    Log("test_sequence_buffer32");
    const int Size = 256;
    var buffer = new SequenceBuffer32<TestPacketData32>(Size);

    for (int i = 0; i < Size; ++i) {
      TestPacketData entry;
      entry.sequence = 0;
      IsTrue(buffer.Exists((uint)i) == false);
      IsTrue(buffer.Available((uint)i) == true);
      IsTrue(buffer.Find((uint)i) == -1);
    }

    for (int i = 0; i <= Size * 4; ++i) {
      int index = buffer.Insert((uint)i);
      IsTrue(index != -1);
      IsTrue(buffer.GetSequence() == i + 1);
      buffer.entries[index].sequence = (uint)i;
    }

    for (int i = 0; i <= Size; ++i) {      
      int index = buffer.Insert((uint)i); //note: outside bounds!
      IsTrue(index == -1);
    }

    uint sequence = Size * 4;
    for (int i = 0; i < Size; ++i) {
      int index = buffer.Find(sequence);
      IsTrue(index >= 0);
      IsTrue(index < Size);
      IsTrue(buffer.entries[index].sequence == sequence);
      sequence--;
    }

    buffer.Reset();
    IsTrue(buffer.GetSequence() == 0);

    for (int i = 0; i < Size; ++i) {
      IsTrue(buffer.Exists((uint)i) == false);
      IsTrue(buffer.Available((uint)i) == true);
      IsTrue(buffer.Find((uint)i) == -1);
    }
  }

  static void test_generate_ack_bits() {
    Log("test_generate_ack_bits");
    const int Size = 256;
    var receivedPackets = new SequenceBuffer<TestPacketData>(Size);
    ushort ack = 0xFFFF;
    uint ack_bits = 0xFFFFFFFF;

    GenerateAckBits(receivedPackets, out ack, out ack_bits);
    IsTrue(ack == 0xFFFF);
    IsTrue(ack_bits == 0);

    for (int i = 0; i <= Size; ++i)
      receivedPackets.Insert((ushort)i);

    GenerateAckBits(receivedPackets, out ack, out ack_bits);
    IsTrue(ack == Size);
    IsTrue(ack_bits == 0xFFFFFFFF);

    receivedPackets.Reset();
    ushort[] input_acks = { 1, 5, 9, 11 };

    for (int i = 0; i < input_acks.Length; ++i)
      receivedPackets.Insert(input_acks[i]);

    GenerateAckBits(receivedPackets, out ack, out ack_bits);
    IsTrue(ack == 11);
    IsTrue(ack_bits == (1 | (1 << (11 - 9)) | (1 << (11 - 5)) | (1 << (11 - 1))));
  }

  static void test_connection() {
    Log("test_connection");
    var sender = new Connection();
    var receiver = new Connection();
    const int NumIterations = 256;

    for (int i = 0; i < NumIterations; ++i) {
      PacketHeader senderPacketHeader;
      PacketHeader receiverPacketHeader;
      sender.GeneratePacketHeader(out senderPacketHeader);
      receiver.GeneratePacketHeader(out receiverPacketHeader);

      if ((i % 11) != 0)
        sender.ProcessPacketHeader(ref receiverPacketHeader);

      if ((i % 13) != 0)
        receiver.ProcessPacketHeader(ref senderPacketHeader);
    }

    var senderAcks = new ushort[Network.Connection.MaximumAcks];
    var receiverAcks = new ushort[Network.Connection.MaximumAcks];
    int numSenderAcks = 0;
    int numReceiverAcks = 0;

    sender.GetAcks(ref senderAcks, ref numSenderAcks);
    receiver.GetAcks(ref receiverAcks, ref numReceiverAcks);
    IsTrue(numSenderAcks > NumIterations / 2);
    IsTrue(numReceiverAcks > NumIterations / 2);

    var senderAcked = new bool[NumIterations];
    var receiverAcked = new bool[NumIterations];

    for (int i = 0; i < NumIterations / 2; ++i) {
      senderAcked[senderAcks[i]] = true;
      receiverAcked[receiverAcks[i]] = true;
    }

    for (int i = 0; i < NumIterations / 2; ++i) {
      IsTrue(senderAcked[i] == ((i % 13) != 0));
      IsTrue(receiverAcked[i] == ((i % 11) != 0));
    }
  }

  static void test_delta_buffer() {
#if !DEBUG_AUTHORITY
    Log("test_delta_buffer");
    const int NumCubeStates = 5;
    const int DeltaBufferSize = 256;
    const ushort Sequence = 100; //check that querying for a sequence number not in the buffer returns false
    const ushort ResetSequence = 1000;
    var buffer = new DeltaBuffer(DeltaBufferSize);
    var state = CubeState.defaults;    
    var result = buffer.GetCube(Sequence, ResetSequence, 0, ref state);

    IsTrue(result == false);    
    result = buffer.AddPacket(Sequence, ResetSequence); //now add an entry for the sequence number
    IsTrue(result);    

    var cubeIds = new int[NumCubeStates]; //add a few cube states for the packet
    var cubeStates = new CubeState[NumCubeStates];

    for (int i = 0; i < NumCubeStates; ++i) {
      cubeStates[i] = CubeState.defaults;
      cubeStates[i].positionX = i;
      int cubeId = 10 + i * 10;
      cubeIds[i] = cubeId;
      result = buffer.AddCube(Sequence, cubeId, ref cubeStates[i]);
      IsTrue(result);
    }    

    for (int i = 0; i < NumCubeStates; ++i) { //verify that we can find the cube state we added by cube id and sequence
      int cubeId = 10 + i * 10;
      result = buffer.GetCube(Sequence, ResetSequence, cubeId, ref state);
      IsTrue(result);
      IsTrue(state.positionX == cubeStates[i].positionX);
    }    

    for (int i = 0; i < Constants.MaxCubes; ++i) { //verify that get cube state returns false for cube ids that weren't in this packet
      var validCubeId = false;

      for (int j = 0; j < NumCubeStates; ++j) {
        if (cubeIds[j] == i)
          validCubeId = true;
      }

      if (validCubeId) continue;

      result = buffer.GetCube(Sequence, ResetSequence, i, ref state);
      IsTrue(result == false);
    }    

    int packetNumCubes; //grab the packet data for the sequence and make sure it matches what we expect
    int[] packetCubeIds;
    CubeState[] packetCubeState;
    result = buffer.GetPacket(Sequence, ResetSequence, out packetNumCubes, out packetCubeIds, out packetCubeState);
    IsTrue(result == true);
    IsTrue(packetNumCubes == NumCubeStates);

    for (int i = 0; i < NumCubeStates; ++i) {
      IsTrue(packetCubeIds[i] == 10 + i * 10);
      IsTrue(packetCubeState[i].positionX == cubeStates[i].positionX);
    }    

    result = buffer.GetPacket(Sequence + 1, ResetSequence, out packetNumCubes, out packetCubeIds, out packetCubeState); //try to grab packet data for an invalid sequence number and make sure it returns false
    IsTrue(result == false);
    
    result = buffer.GetPacket(Sequence, ResetSequence + 1, out packetNumCubes, out packetCubeIds, out packetCubeState); //try to grab packet data for a different reset sequence number and make sure it returns false
    IsTrue(result == false);
#endif // #if !DEBUG_AUTHORITY
  }

  static void test_signed_unsigned() {
    Log("test_signed_unsigned");
    var expectedValues = new[] { 0, -1, 1, -2, 2, -3, 3, -4, 4, -5, 5, -6, 6 };

    for (int i = 0; i < expectedValues.Length; ++i) {
      int signed = UnsignedToSigned((uint)i);
      IsTrue(signed == expectedValues[i]);

      var unsigned = SignedToUnsigned(signed);
      IsTrue(unsigned == (uint)i);
    }
  }

  public static void RunTests() {
    raiseExceptions = true;
    test_bitpacker();
    test_serialization();
    test_sequence_buffer();
    test_sequence_buffer32();
    test_generate_ack_bits();
    test_connection();
    test_delta_buffer();
    test_signed_unsigned();
    Log("All tests completed successfully!");
  }
}