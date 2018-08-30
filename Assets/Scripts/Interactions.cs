/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using UnityEngine.Assertions;
using static Constants;

public class Interactions {
  public class Entry {
    public byte[] interactions = new byte[NumCubes];

    public void Add(ushort id) => interactions[id] = 1;
    public void Remove(ushort id) => interactions[id] = 0;
  }

  Entry[] entries = new Entry[NumCubes];

  public Interactions() {
    for (int i = 0; i < NumCubes; ++i)
      entries[i] = new Entry();
  }

  public void Add(ushort id1, ushort id2) {
    entries[id1].Add(id2);
    entries[id2].Add(id1);
  }

  public void Remove(ushort id1, ushort id2) {
    entries[id1].Remove(id2);
    entries[id2].Remove(id1);
  }

  public Entry Get(int cubeId) {
    Assert.IsTrue(cubeId >= 0);
    Assert.IsTrue(cubeId < NumCubes);

    return entries[cubeId];
  }
}