/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using System;
using static UnityEngine.Assertions.Assert;
using static Network.Constants;
using static Network.Util;
using static System.BitConverter;
using static System.Buffer;

namespace Network {
  public static class Constants {
    public const int MaxStringLength = 255;
    public const int STREAM_ERROR_NONE = 0;
    public const int STREAM_ERROR_OVERFLOW = 1;
    public const int STREAM_ERROR_ALIGNMENT = 2;
    public const int STREAM_ERROR_VALUE_OUT_OF_RANGE = 3;
  };

  public static class Util {
    public static uint SignedToUnsigned(int n) => (uint)((n << 1) ^ (n >> 31));
    public static int UnsignedToSigned(uint n) => (int)((n >> 1) ^ (-(n & 1)));

    public static void GenerateAckBits<T>(SequenceBuffer<T> buffer, out ushort ack, out uint ackBits) {
      ack = (ushort)(buffer.GetSequence() - 1);
      ackBits = 0;
      uint mask = 1;

      for (int i = 0; i < 32; ++i) {
        if (buffer.Exists((ushort)(ack - i)))
          ackBits |= mask;

        mask <<= 1;
      }
    }

    public static bool SequenceGreaterThan(ushort first, ushort second) 
      => ((first > second) && (first-second <= 32768)) 
      || ((first < second) && (second-first > 32768));

    public static bool SequenceLessThan(ushort first, ushort second) => SequenceGreaterThan(second, first);

    public static int BaselineDifference(ushort current, ushort baseline) {
      if (current > baseline)
        return current - baseline;

      return (ushort)(((uint)current) + 65536 - baseline);
    }

    public static uint SwapBytes(uint value) 
      => ((value & 0x000000FF) << 24) 
       | ((value & 0x0000FF00) << 8) 
       | ((value & 0x00FF0000) >> 8) 
       | ((value & 0xFF000000) >> 24);

    public static uint HostToNetwork(uint value) { //why not one method instead?
      if (IsLittleEndian)
        return value; 

      return SwapBytes(value);
    }

    public static uint NetworkToHost(uint value) {
      if (IsLittleEndian)
        return value;

      return SwapBytes(value);
    }

    public static int PopCount(uint v) {
      v = v - ((v >> 1) & 0x55555555); //240 => 127 - 1111111
      v = (v & 0x33333333) + ((v >> 2) & 0x33333333); //(106 & 110011001100110011001100110011 = 22) + (53 & 110011001100110011001100110011 = 49) = 155
      v = ((v + (v >> 4) & 0xF0F0F0F) * 0x1010101) >> 24; //(((155 + 9) & 1111000011110000111100001111 = 4) * 1000000010000000100000001) = 4

      return unchecked((int)v);
    }

    public static int Log2(uint x) { //fill all bits from the first and shift result by one
      var a = x | (x >> 1);
      var b = a | (a >> 2);
      var c = b | (b >> 4);
      var d = c | (c >> 8);
      var e = d | (d >> 16);
      var f = e >> 1;

      return PopCount(f);
    }

    public static int BitsRequired(int min, int max) => (min == max) ? 1 : Log2((uint)(max - min)) + 1;
    public static int BitsRequired(uint min, uint max) => (min == max) ? 1 : Log2(max - min) + 1;
  }

  public class BitWriter {
    uint[] words;
    ulong scratch;
    int bitCount;
    int wordCount;
    int bitsWritten;
    int wordId;
    int scratchBits;

    public void Start(uint[] data) {
      IsTrue(data != null);
      words = data;
      wordCount = data.Length / 4;
      bitCount = wordCount * 32;
      bitsWritten = 0;
      wordId = 0;
      scratch = 0;
      scratchBits = 0;
    }

    public void Bits(uint value, int bits) {
      IsTrue(bits > 0);
      IsTrue(bits <= 32);
      IsTrue(bitsWritten + bits <= bitCount);
      value &= (uint)((((ulong)1) << bits) - 1);
      scratch |= ((ulong)value) << scratchBits;
      scratchBits += bits;

      if (scratchBits >= 32) {
        IsTrue(wordId < wordCount);
        words[wordId] = HostToNetwork((uint)(scratch & 0xFFFFFFFF));
        scratch >>= 32;
        scratchBits -= 32;
        wordId++;
      }
      bitsWritten += bits;
    }

    public void Align() {
      int remainderBits = bitsWritten % 8;
      if (remainderBits == 0) return;

      Bits(0, 8 - remainderBits);
      IsTrue((bitsWritten % 8) == 0);
    }

    public void Bytes(byte[] data, int bytes) {
      IsTrue(GetAlignBits() == 0);

      for (int i = 0; i < bytes; ++i)
        Bits(data[i], 8);
    }

    public void Finish() {
      if (scratchBits == 0) return;

      IsTrue(wordId < wordCount);
      words[wordId] = HostToNetwork((uint)(scratch & 0xFFFFFFFF));
      scratch >>= 32;
      scratchBits -= 32;
      wordId++;
    }

    public int GetAlignBits() => (8 - (bitsWritten % 8)) % 8; //is not bitsWritten % 8 enought?
    public int GetBitsWritten() => bitsWritten;
    public int GetBitsAvailable() => bitCount - bitsWritten;

    public byte[] GetData() {
      int count = GetBytesWritten();
      var output = new byte[count];
      BlockCopy(words, 0, output, 0, count);

      return output;
    }

    public int GetBytesWritten() => (bitsWritten + 7) / 8;
    public int GetTotalBytes() => wordCount * 4;
  }

  public class BitReader {
    uint[] words;
    ulong scratch;
    int bitCount;
    int wordCount;
    int bitsRead;
    int scratchBits;
    int wordId;

    public void Start(byte[] data) {
      int count = data.Length;
      wordCount = (count + 3) / 4;
      bitCount = count * 8;
      bitsRead = 0;
      scratch = 0;
      scratchBits = 0;
      wordId = 0;
      words = new uint[wordCount];
      BlockCopy(data, 0, words, 0, count);
    }

    public bool WouldOverflow(int bits) => bitsRead + bits > bitCount;

    public uint Bits(int bits) {
      IsTrue(bits > 0);
      IsTrue(bits <= 32);
      IsTrue(bitsRead + bits <= bitCount);
      bitsRead += bits;
      IsTrue(scratchBits >= 0 && scratchBits <= 64);

      if (scratchBits < bits) {
        IsTrue(wordId < wordCount);
        scratch |= ((ulong)NetworkToHost(words[wordId])) << scratchBits;
        scratchBits += 32;
        wordId++;
      }
      IsTrue(scratchBits >= bits);

      var output = (uint)(scratch & ((((ulong)1) << bits) - 1));
      scratch >>= bits;
      scratchBits -= bits;

      return output;
    }

    public bool Align() {
      int remainderBits = bitsRead % 8;
      if (remainderBits == 0) return true;

      uint value = Bits(8 - remainderBits);
      IsTrue(bitsRead % 8 == 0);
      return value == 0;
    }

    public void Bytes(byte[] data, int bytes) {
      IsTrue(GetAlignBits() == 0);

      for (int i = 0; i < bytes; ++i)
        data[i] = (byte)Bits(8);
    }

    public void Finish() { /* ...*/}
    public int GetAlignBits() => (8 - bitsRead % 8) % 8;
    public int GetBitsRead() => bitsRead;
    public int GetBytesRead() => wordId * 4;
    public int GetBitsRemaining() => bitCount - bitsRead;
    public int GetBytesRemaining() => GetBitsRemaining() / 8;
  }

  public class WriteStream {
    BitWriter w = new BitWriter();
    int error = STREAM_ERROR_NONE;

    public void Start(uint[] buffer) => w.Start(buffer);

    public void Int(int value, int min, int max) {
      IsTrue(min < max);
      IsTrue(value >= min);
      IsTrue(value <= max);
      w.Bits((uint)(value - min), BitsRequired(min, max));
    }

    public void Uint(uint value, uint min, uint max) {
      IsTrue(min < max);
      IsTrue(value >= min);
      IsTrue(value <= max);
      w.Bits(value - min, BitsRequired(min, max));
    }

    public void Bits(byte value, int bits) {
      IsTrue(bits > 0);
      IsTrue(bits <= 8);
      IsTrue(bits == 8 || (value < (1 << bits)));
      w.Bits(value, bits);
    }

    public void Bits(ushort value, int bits) {
      IsTrue(bits > 0);
      IsTrue(bits <= 16);
      IsTrue(bits == 16 || (value < (1 << bits)));
      w.Bits(value, bits);
    }

    public void Bits(uint value, int bits) {
      IsTrue(bits > 0);
      IsTrue(bits <= 32);
      IsTrue(bits == 32 || (value < (1 << bits)));
      w.Bits(value, bits);
    }

    public void Bits(ulong value, int bits) {
      IsTrue(bits > 0);
      IsTrue(bits <= 64);
      IsTrue(bits == 64 || (value < (1UL << bits)));

      if (bits <= 32)
        w.Bits((uint)value, bits);
      else {
        w.Bits((uint)value, 32);
        w.Bits((uint)(value >> 32), bits - 32);
      }
    }

    public void Bytes(byte[] data, int bytes) {
      IsTrue(data != null);
      IsTrue(bytes >= 0);
      w.Align();
      w.Bytes(data, bytes);
    }

    public void String(string s) {
      w.Align();
      IsTrue(s.Length <= MaxStringLength);
      w.Bits((byte)s.Length, BitsRequired(0, MaxStringLength));

      for (int i = 0; i < s.Length; ++i)
        w.Bits(s[i], 16);
    }

    public void Float(float f) {
      var bytes = GetBytes(f);

      for (int i = 0; i < 4; ++i)
        w.Bits(bytes[i], 8);
    }

    public void Bool(bool b) => w.Bits(b ? 1U : 0U, 1);
    public void Finish() => w.Finish();
    public byte[] GetData() => w.GetData();
  }

  public class ReadStream {
    BitReader r = new BitReader();
    byte[] floatBytes = new byte[4];
    int bitsRead = 0;
    int error = STREAM_ERROR_NONE;

    public void Start(byte[] packet) => r.Start(packet);

    public bool Int(out int value, int min, int max) {
      IsTrue(min < max);
      int bits = BitsRequired(min, max);

      if (r.WouldOverflow(bits)) {
        error = STREAM_ERROR_OVERFLOW;
        value = 0;
        throw new SerializeException();
      }

      value = (int)(r.Bits(bits) + min);
      bitsRead += bits;
      return true;
    }

    public bool Uint(out uint value, uint min, uint max) {
      IsTrue(min < max);
      int bits = BitsRequired(min, max);

      if (r.WouldOverflow(bits)) {
        error = STREAM_ERROR_OVERFLOW;
        value = 0;
        throw new SerializeException();
      }

      value = r.Bits(bits) + min;
      bitsRead += bits;
      return true;
    }

    public bool Bits(out byte value, int bits) {
      IsTrue(bits > 0);
      IsTrue(bits <= 8);

      if (r.WouldOverflow(bits)) {
        error = STREAM_ERROR_OVERFLOW;
        value = 0;
        throw new SerializeException();
      }

      value = (byte)r.Bits(bits);
      bitsRead += bits;
      return true;
    }

    public bool Bits(out ushort value, int bits) {
      IsTrue(bits > 0);
      IsTrue(bits <= 16);

      if (r.WouldOverflow(bits)) {
        error = STREAM_ERROR_OVERFLOW;
        value = 0;
        throw new SerializeException();
      }

      value = (ushort)r.Bits(bits);
      bitsRead += bits;
      return true;
    }

    public bool Bits(out uint value, int bits) {
      IsTrue(bits > 0);
      IsTrue(bits <= 32);

      if (r.WouldOverflow(bits)) {
        error = STREAM_ERROR_OVERFLOW;
        value = 0;
        throw new SerializeException();
      }

      value = r.Bits(bits);
      bitsRead += bits;
      return true;
    }

    public bool Bits(out ulong value, int bits) {
      IsTrue(bits > 0);
      IsTrue(bits <= 64);

      if (r.WouldOverflow(bits)) {
        error = STREAM_ERROR_OVERFLOW;
        value = 0;
        throw new SerializeException();
      }

      if (bits <= 32) value = r.Bits(bits);
      else {
        var loword = r.Bits(32);
        var hiword = r.Bits(bits - 32);
        value = loword | (((ulong)hiword) << 32);
      }
      return true;
    }

    public bool Bytes(byte[] data, int bytes) {
      if (!Align()) return false;

      if (r.WouldOverflow(bytes * 8)) {
        error = STREAM_ERROR_OVERFLOW;
        throw new SerializeException();
      }

      r.Bytes(data, bytes);
      bitsRead += bytes * 8;
      return true;
    }

    public bool String(out string s) {
      if (!Align()) {
        s = null;
        throw new SerializeException();
      }

      int length;
      if (!Int(out length, 0, MaxStringLength)) {
        s = null;
        throw new SerializeException();
      }

      if (r.WouldOverflow(length * 16)) {
        error = STREAM_ERROR_OVERFLOW;
        s = null;
        throw new SerializeException();
      }

      var data = new char[MaxStringLength];

      for (int i = 0; i < length; ++i)
        data[i] = (char)r.Bits(16);

      s = new string(data, 0, length);
      return true;
    }

    public bool Float(out float f) {
      if (r.WouldOverflow(32)) {
        error = STREAM_ERROR_OVERFLOW;
        f = 0.0f;
        throw new SerializeException();
      }

      for (int i = 0; i < 4; ++i)
        floatBytes[i] = (byte)r.Bits(8);

      f = ToSingle(floatBytes, 0);
      return true;
    }

    public bool Align() {
      int bits = r.GetAlignBits();

      if (r.WouldOverflow(bits)) {
        error = STREAM_ERROR_OVERFLOW;
        throw new SerializeException();
      }

      if (!r.Align()) {
        error = STREAM_ERROR_ALIGNMENT;
        throw new SerializeException();
      }

      bitsRead += bits;
      return true;
    }

    public void Bool(out bool b) {
      uint value;

      if (!Bits(out value, 1))
        throw new SerializeException();

      b = (value == 1) ? true : false;
    }

    public void Finish() => r.Finish();
    public int GetAlignBits() => r.GetAlignBits();
    public int GetBitsProcessed() => bitsRead;
    public int GetBytesProcessed() => (bitsRead + 7) / 8;
    public int GetError() => error;
  }

  public class SerializeException : Exception {
    public SerializeException() { }
  }

  public class SequenceBuffer<T> {
    public T[] entries;
    uint[] entriesSequence;
    int size;
    ushort sequence;

    public SequenceBuffer(int size) {
      IsTrue(size > 0);
      this.size = size;
      sequence = 0;
      entriesSequence = new uint[size];
      entries = new T[size];
      Reset();
    }

    public void Reset() {
      sequence = 0;

      for (int i = 0; i < size; ++i)
        entriesSequence[i] = 0xFFFFFFFF;
    }

    public int Insert(ushort newSequence) {
      if (SequenceGreaterThan((ushort)(newSequence + 1), sequence)) {
        RemoveEntries(sequence, newSequence);
        sequence = (ushort)(newSequence + 1);

      } else if (SequenceLessThan(newSequence, (ushort)(sequence - size))) {
        return -1;
      }

      int id = newSequence % size;
      entriesSequence[id] = newSequence;
      return id;
    }

    public void Remove(ushort sequence) => entriesSequence[sequence % size] = 0xFFFFFFFF;
    public bool Available(ushort sequence) => entriesSequence[sequence % size] == 0xFFFFFFFF;
    public bool Exists(ushort sequence) => entriesSequence[sequence % size] == sequence;

    public int Find(ushort sequence) {
      int id = sequence % size;

      if (entriesSequence[id] == sequence)
        return id;

      return -1;
    }

    public ushort GetSequence() => sequence;
    public int GetSize() => size;

    public void RemoveEntries(ushort startSequence, ushort finishSequence) {
      int finish = finishSequence < startSequence
        ? finishSequence + 65535
        : finishSequence;

      for (int sequence = startSequence; sequence <= finish; ++sequence)
        entriesSequence[sequence % size] = 0xFFFFFFFF;
    }
  }

  public class SequenceBuffer32<T> where T : new() {
    public T[] entries;
    uint[] entrySequence;
    int size;
    uint sequence;

    public SequenceBuffer32(int size) {
      IsTrue(size > 0);
      this.size = size;
      sequence = 0;
      entrySequence = new uint[size];
      entries = new T[size];

      for (int i = 0; i < size; ++i)
        entries[i] = new T();

      Reset();
    }

    public void Reset() {
      sequence = 0;

      for (int i = 0; i < size; ++i)
        entrySequence[i] = 0xFFFFFFFF;
    }

    public int Insert(uint newSequence) {
      IsTrue(newSequence != 0xFFFFFFFF);

      if (newSequence + 1 > sequence) {
        RemoveEntries(sequence, newSequence);
        sequence = newSequence + 1;

      } else if (newSequence < sequence - size) {
        return -1;
      }

      int id = (int)(newSequence % size);
      entrySequence[id] = newSequence;
      return id;
    }

    public void Remove(uint sequence) {
      IsTrue(sequence != 0xFFFFFFFF);
      entrySequence[sequence % size] = 0xFFFFFFFF;
    }

    public bool Available(uint sequence) {
      IsTrue(sequence != 0xFFFFFFFF);
      return entrySequence[sequence % size] == 0xFFFFFFFF;
    }

    public bool Exists(uint sequence) {
      IsTrue(sequence != 0xFFFFFFFF);
      return entrySequence[sequence % size] == sequence;
    }

    public int Find(uint sequence) {
      IsTrue(sequence != 0xFFFFFFFF);
      int id = (int)(sequence % size);

      if (entrySequence[id] == sequence)
        return id; else
        return -1;
    }

    public uint GetSequence() => sequence;
    public int GetSize() => size;

    public void RemoveEntries(uint start, uint finish) {
      IsTrue(start <= finish);

      if (finish - start < size) {
        for (uint sequence = start; sequence <= finish; ++sequence)
          entrySequence[sequence % size] = 0xFFFFFFFF;

      } else {
        for (int i = 0; i < size; ++i)
          entrySequence[i] = 0xFFFFFFFF;
      }
    }
  }

  public struct PacketHeader {
    public ushort id;
    public ushort ack;
    public uint ackBits;
    public uint frame;                    //physics simulation frame # for jitter buffer
    public ushort resetSequence;                //incremented each time the simulation is reset
    public float timeOffset;                    //offset between the current physics frame time of this packet and the time where the avatar state was sampled
  }

  public struct SentPacketData {
    public bool acked;
  }

  public struct ReceivedPacketData { }

  public class Connection {
    public const int MaximumAcks = 1024;
    public const int SentPacketsSize = 1024;
    public const int ReceivedPacketsSize = 1024;

    ushort id = 0;
    int ackCount = 0;
    ushort[] acks = new ushort[MaximumAcks];
    SequenceBuffer<SentPacketData> sentPackets = new SequenceBuffer<SentPacketData>(SentPacketsSize);
    SequenceBuffer<ReceivedPacketData> receivedPackets = new SequenceBuffer<ReceivedPacketData>(ReceivedPacketsSize);

    public void GeneratePacketHeader(out PacketHeader h) {
      h.id = id;
      GenerateAckBits(receivedPackets, out h.ack, out h.ackBits);
      h.frame = 0;
      h.resetSequence = 0;
      h.timeOffset = 0.0f;
      int newId = sentPackets.Insert(id);
      IsTrue(newId != -1);
      sentPackets.entries[newId].acked = false;
      id++;
    }

    public void ProcessPacketHeader(ref PacketHeader h) {
      PacketReceived(h.id);

      for (int i = 0; i < 32; ++i) {
        if ((h.ackBits & 1) == 0) {
          h.ackBits >>= 1;
          continue;
        }

        var ackedId = (ushort)(h.ack - i);
        int id = sentPackets.Find(ackedId);

        if (id != -1 && !sentPackets.entries[id].acked) {
          PacketAcked(ackedId);
          sentPackets.entries[id].acked = true;
        }
        h.ackBits >>= 1;
      }
    }

    public void GetAcks(ref ushort[] acks, ref int numAcks) {
      for (int i = 0; i < Math.Min(ackCount, acks.Length); ++i)
        acks[i] = this.acks[i];

      ackCount = 0;
    }

    public void Reset() {
      id = 0;
      ackCount = 0;
      sentPackets.Reset();
      receivedPackets.Reset();
    }

    void PacketReceived(ushort id) => receivedPackets.Insert(id);

    void PacketAcked(ushort id) {
      if (ackCount == MaximumAcks - 1) return;

      acks[ackCount++] = id;
    }
  }

  public class Simulator {
    Random random = new Random();
    float latency;                                // latency in milliseconds
    float jitter;                                 // jitter in milliseconds +/-
    float packetLoss;                             // packet loss percentage
    float duplicate;                              // duplicate packet percentage
    double time;                                  // current time from last call to advance time. initially 0.0
    int insertId;                              // current index in the packet entry array. new packets are inserted here.
    int receiveId;                             // current receive index. packets entries are walked until this wraps back around to m_insertInsdex.

    struct PacketEntry {
      public int from;                            // address this packet is from
      public int to;                              // address this packet is sent to
      public double deliveryTime;                 // delivery time for this packet
      public byte[] packet;                   // packet data
    };

    PacketEntry[] packetEntries;                  // packet entries

    public Simulator() {
      packetEntries = new PacketEntry[4 * 1024];
    }

    public void SetLatency(float ms) => latency = ms;
    public void SetJitter(float ms) => jitter = ms;
    public void SetPacketLoss(float percent) => packetLoss = percent;
    public void SetDuplicate(float percent) => duplicate = percent;
    public float RandomFloat(float min, float max) => (float)random.NextDouble() * (max - min) + min;

    public void SendPacket(int from, int to, byte[] packet) {
      if (RandomFloat(0.0f, 100.0f) <= packetLoss) return;

      var delay = latency / 1000.0;

      if (jitter > 0)
        delay += RandomFloat(0, jitter) / 1000.0;

      packetEntries[insertId].from = from;
      packetEntries[insertId].to = to;
      packetEntries[insertId].packet = packet;
      packetEntries[insertId].deliveryTime = time + delay;
      insertId = (insertId + 1) % packetEntries.Length;

      if (RandomFloat(0.0f, 100.0f) > duplicate) return;

      var duplicates = new byte[packet.Length];
      BlockCopy(packet, 0, duplicates, 0, packet.Length);
      packetEntries[insertId].from = from;
      packetEntries[insertId].to = to;
      packetEntries[insertId].packet = packet;
      packetEntries[insertId].deliveryTime = time + delay + RandomFloat(0.0f, 1.0f);
      insertId = (insertId + 1) % packetEntries.Length;
    }

    public byte[] ReceivePacket(out int from, out int to) {
      while (receiveId != insertId) {
        if (packetEntries[receiveId].packet == null && packetEntries[receiveId].deliveryTime > time) {
          receiveId = (receiveId + 1) % packetEntries.Length;
          continue;
        }

        var packet = packetEntries[receiveId].packet;
        from = packetEntries[receiveId].from;
        to = packetEntries[receiveId].to;
        packetEntries[receiveId].packet = null;
        receiveId = (receiveId + 1) % packetEntries.Length;
        return packet;
      }

      from = 0;
      to = 0;
      return null;
    }

    public void AdvanceTime(double newTime) {
      time = newTime;
      receiveId = (insertId + 1) % packetEntries.Length;
    }
  }
}