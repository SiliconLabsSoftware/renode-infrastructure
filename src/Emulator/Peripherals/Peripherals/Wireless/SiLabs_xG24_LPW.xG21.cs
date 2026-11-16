//
// Copyright 2026 ADI Global Distribution LLC. All Rights Reserved.
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
// The EFR32xG21 register layout for SiLabs_xG24_LPW. The offset tables are generated
// (SiLabs_xG24_LPW.xG21Layout.cs); this half holds the translation the bus read and write paths
// apply when the peripheral is constructed with registerLayout: "xg21", and the "bufc" regions
// that give the separate buffer controller model an xG21 register map.
using System.Collections.Generic;

using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Wireless
{
    public partial class SiLabs_xG24_LPW
    {
        // The buffer controller is a peripheral of its own with the xG24 register map. On xG21 it
        // is left off the bus and reached through these regions instead, which translate each
        // offset and pass the access on. With the "xg24" layout the access is passed on unchanged.
        [ConnectionRegion("bufc")]
        public uint ReadDoubleWordFromBufferController(long offset)
        {
            return ReadBufferController(offset);
        }

        [ConnectionRegion("bufc")]
        public byte ReadByteFromBufferController(long offset)
        {
            return ReadByteFromBufferControllerRegister(offset);
        }

        [ConnectionRegion("bufc")]
        public void WriteDoubleWordToBufferController(long offset, uint value)
        {
            WriteBufferController(offset, value);
        }

        [ConnectionRegion("bufc")]
#pragma warning disable IDE0060
        public void WriteByteToBufferController(long offset, byte value)
#pragma warning restore IDE0060
        {
            // Single byte writes are ignored, as in the other register blocks.
        }

        [ConnectionRegion("bufc_ns")]
        public uint ReadDoubleWordFromBufferControllerNonSecure(long offset)
        {
            return ReadBufferController(offset);
        }

        [ConnectionRegion("bufc_ns")]
        public byte ReadByteFromBufferControllerNonSecure(long offset)
        {
            return ReadByteFromBufferControllerRegister(offset);
        }

        [ConnectionRegion("bufc_ns")]
        public void WriteDoubleWordToBufferControllerNonSecure(long offset, uint value)
        {
            WriteBufferController(offset, value);
        }

        [ConnectionRegion("bufc_ns")]
#pragma warning disable IDE0060
        public void WriteByteToBufferControllerNonSecure(long offset, byte value)
#pragma warning restore IDE0060
        {
            // Single byte writes are ignored, as in the other register blocks.
        }

        private uint ReadBufferController(long offset)
        {
            var registerOffset = offset & XG21RegisterOffsetMask;
            if(xG21Layout && TranslateXG21(xG21BufcToModel, xG21BufcOnly, xG21BufcStorage, ref registerOffset, out var storage) && storage != null)
            {
                return storage.TryGetValue(registerOffset, out var stored) ? stored : 0u;
            }
            return bufferController.ReadDoubleWord(registerOffset);
        }

        private byte ReadByteFromBufferControllerRegister(long offset)
        {
            var byteOffset = (int)(offset & 0x3);
            return (byte)(ReadBufferController(offset - byteOffset) >> (byteOffset * 8));
        }

        private void WriteBufferController(long offset, uint value)
        {
            var registerOffset = offset & XG21RegisterOffsetMask;
            var aliasOffset = offset - registerOffset;
            if(xG21Layout && TranslateXG21(xG21BufcToModel, xG21BufcOnly, xG21BufcStorage, ref registerOffset, out var storage) && storage != null)
            {
                storage.TryGetValue(registerOffset, out var stored);
                switch(aliasOffset)
                {
                case SetRegisterOffset:
                    value = stored | value;
                    break;
                case ClearRegisterOffset:
                    value = stored & ~value;
                    break;
                case ToggleRegisterOffset:
                    value = stored ^ value;
                    break;
                }
                storage[registerOffset] = value;
                return;
            }
            // The buffer controller folds the SET/CLR/TGL alias itself.
            bufferController.WriteDoubleWord(aliasOffset + registerOffset, value);
        }

        // Translates a bus offset, already folded from its SET/CLR/TGL alias, in a block xG21 lays
        // out differently. Returns true when the offset is known: offset is then the model's own
        // register and storage is null, or the register exists only on xG21 and is kept in storage
        // at its xG21 offset. Returns false for blocks that share the xG24 layout and for offsets
        // neither table knows; those take the ordinary decode.
        private bool TranslateXG21<T>(ref long offset, out Dictionary<long, uint> storage)
        {
            if(typeof(T) == typeof(ModulatorAndDemodulatorRegisters))
            {
                return TranslateXG21(xG21ModemToModel, xG21ModemOnly, xG21ModemStorage, ref offset, out storage);
            }
            if(typeof(T) == typeof(RadioControllerRegisters))
            {
                return TranslateXG21(xG21RacToModel, xG21RacOnly, xG21RacStorage, ref offset, out storage);
            }
            storage = null;
            return false;
        }

        private static bool TranslateXG21(Dictionary<long, long> toModel, HashSet<long> only, Dictionary<long, uint> onlyStorage, ref long offset, out Dictionary<long, uint> storage)
        {
            storage = null;
            if(toModel.TryGetValue(offset, out var modelOffset))
            {
                offset = modelOffset;
                return true;
            }
            if(only.Contains(offset))
            {
                storage = onlyStorage;
                return true;
            }
            return false;
        }

        private void ResetXG21Storage()
        {
            xG21BufcStorage.Clear();
            xG21ModemStorage.Clear();
            xG21RacStorage.Clear();
        }

        private readonly bool xG21Layout;
        private readonly Dictionary<long, uint> xG21BufcStorage = new Dictionary<long, uint>();
        private readonly Dictionary<long, uint> xG21ModemStorage = new Dictionary<long, uint>();
        private readonly Dictionary<long, uint> xG21RacStorage = new Dictionary<long, uint>();

        private const long XG21RegisterOffsetMask = 0x0FFF;
    }
}
