﻿/*
 * Copyright 2014 Bryan Davis
 * Licensed under GPLv2
 * Refer to the license.txt file included
 */

using SharpBoy.Cartridge;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SharpBoy.Core
{
    [Serializable]
    public partial class CPU
    {
        public Register RegisterAF { get; private set; }
        public Register RegisterBC { get; private set; }
        public Register RegisterDE { get; private set; }
        public Register RegisterHL { get; private set; }
        public Register StackPointer { get; private set; }
        public ushort ProgramCounter { get; private set; }

        // Flags; http://problemkaputt.de/pandocs.htm#cpuregistersandflags
        public readonly byte FlagZ = 1 << 7;    // Zero Flag
        public readonly byte FlagN = 1 << 6;    // Add/Sub Flag
        public readonly byte FlagH = 1 << 5;    // Half Carry Flag            
        public readonly byte FlagC = 1 << 4;    // Carry Flag

        public MemoryBankController Memory { get; private set; }
        public CartridgeInfo CartInfo { get; private set; }
        public bool Halted { get; private set; }
        public bool Stopped { get; private set; }

        public Interrupts Interrupts { get; private set; }
        public bool InterruptsEnabled { get; private set; }
        public bool TimerEnabled { get; private set; }
        public int TimerCycleIncrement { get; private set; }
        public int TimerCycles { get; private set; }
        public int DividerCycleIncrement { get; private set; }
        public int DividerCycles { get; private set; }

        public int ScanlineCounter { get; private set; }

        public CPU() { }

        public void LoadRom(string romPath)
        {
            byte[] cartHeader = new byte[0x150];    // Header is 336 bytes
            using (Stream fileStream = File.OpenRead(romPath))
            {
                fileStream.Read(cartHeader, 0x0, cartHeader.Length);
                CartInfo = new CartridgeInfo(cartHeader);
                fileStream.Seek(0, SeekOrigin.Begin);
                Memory = CreateMBC(CartInfo.CartType, fileStream);
            }
            Memory.UpdateTimerHandler += HandleTimerUpdate;
            Memory.InterruptEnableHandler += HandleInterruptEnable;
            Memory.InterruptRequestHandler += HandleInterruptRequest;
            Reset();
        }

        public byte ReadNextValue()
        {
            byte value = Memory[ProgramCounter++];
            return value;
        }

        // TODO: Verify correct endianess is used
        public ushort ReadNextTwoValues()
        {
            byte low = Memory[ProgramCounter++];
            ushort values = low;
            byte high = Memory[ProgramCounter++];
            values |= (ushort)(high << 8);
            return values;
        }

        public void UpdateGraphics(int cycleCount)
        {
            // TODO: So much more to do!

            if (!LCDEnabled())
                return;
        }

        /*
         * http://problemkaputt.de/pandocs.htm#timeranddividerregisters
         * TimerCycleIncrement is the number of cycles that need to have happened
         * for the TIMA timer counter to increment by one. TimerCycles keeps track
         * of the number of cycles that have occured between each increment, resetting
         * to 0 after an increment has occurred.
         */
        public void HandleTimers(int cycleCount)
        {
            HandleDivider(cycleCount);

            if (TimerEnabled)
            {
                TimerCycles += cycleCount;
                if (TimerCycles >= TimerCycleIncrement)
                {
                    int result = Memory[0xFF05] + 1;
                    if (result > 255)
                    {
                        Memory[0xFF05] = Memory[0xFF06];
                        Interrupts.TimerRequested = true;
                    }
                    else
                    {
                        Memory[0xFF05] = (byte)result;
                    }                    
                    TimerCycles = 0;
                } 
            }
        }

        /*
         * Bit 2    - Timer Stop  (0=Stop, 1=Start)
         * Bits 1-0 - Input Clock Select
         * 00:   4096 Hz
         * 01: 262144 Hz
         * 10:  65536 Hz
         * 11:  16384 Hz
         */
        public void HandleTimerUpdate(byte value)
        {
            //Debug.WriteLine("Timer handler triggered");
            TimerEnabled = BitSet(2, value);

            int cyclesPerSecond = Properties.Settings.Default.cyclesPerSecond;
            byte refreshSetting = (byte)(value & 3);
            switch (refreshSetting)
            {
                case 0: TimerCycleIncrement = cyclesPerSecond / 4096;
                    break;
                case 1: TimerCycleIncrement = cyclesPerSecond / 262144; 
                    break;
                case 2: TimerCycleIncrement = cyclesPerSecond / 65536; 
                    break;
                case 3: TimerCycleIncrement = cyclesPerSecond / 16384; 
                    break;
                default:
                    break;
            }
        }

        /*
         * Bit 0: V-Blank 
         * Bit 1: LCD STAT
         * Bit 2: Timer     
         * Bit 4: Joypad  
         */
        public void HandleInterruptEnable(byte value)
        {
            //Debug.WriteLine("Interrupt enable handler triggered");
            if (BitSet(0, value)) Interrupts.VBlankEnabled = true;
            if (BitSet(1, value)) Interrupts.LCDEnabled = true;
            if (BitSet(2, value)) Interrupts.TimerEnabled = true;
            if (BitSet(4, value)) Interrupts.JoypadEnabled = true;
        }

        /*
         * Bit 0: V-Blank 
         * Bit 1: LCD STAT
         * Bit 2: Timer     
         * Bit 4: Joypad  
         */
        public void HandleInterruptRequest(byte value)
        {
            //Debug.WriteLine("Interrupt request handler triggered");
            if (BitSet(0, value)) Interrupts.VBlankRequested = true;
            if (BitSet(1, value)) Interrupts.LCDRequested = true;
            if (BitSet(2, value)) Interrupts.TimerRequested = true;
            if (BitSet(4, value)) Interrupts.JoypadRequested = true;
        }

        // http://problemkaputt.de/pandocs.htm#interrupts
        public void ProcessInterrupts()
        {
            if (InterruptsEnabled)
            {
                InterruptsEnabled = false;
                ushort addressJump = 0x0000;

                Debug.WriteLine(Interrupts);

                if (Interrupts.VBlankEnabled && Interrupts.VBlankRequested)
                {
                    addressJump = 0x0040;
                    Interrupts.VBlankRequested = false;
                }
                else if (Interrupts.LCDEnabled && Interrupts.LCDRequested)
                {
                    addressJump = 0x0048;
                    Interrupts.LCDRequested = false;
                }
                else if (Interrupts.TimerEnabled && Interrupts.TimerRequested)
                {
                    addressJump = 0x0050;
                    Interrupts.TimerRequested = false;
                }
                else if (Interrupts.JoypadEnabled && Interrupts.JoypadRequested)
                {
                    addressJump = 0x0060;
                    Interrupts.JoypadRequested = false;
                }

                if (addressJump != 0x0000)
                {
                    PushAddressOntoStack(ProgramCounter);
                    ProgramCounter = addressJump;
                }                
            }
        }        

        private void HandleDivider(int cycleCount)
        {
            DividerCycles += cycleCount;
            if (DividerCycles >= DividerCycleIncrement)
            {
                Memory.IncrementDividerRegister();
                DividerCycles = 0;
            } 
        }

        public void ExecuteOpCode(byte opCode)
        {
            switch (opCode)
            {
                case 0x00: // Do nothing
                    break;
                case 0x01: LoadValueToRegister16Bit(RegisterBC);
                    break;
                case 0x02: LoadValueToMemory8Bit(RegisterBC.Value, RegisterAF.High);
                    break;
                case 0x03: IncrementRegister16Bit(RegisterBC);
                    break;
                case 0x04: IncrementRegister8Bit(ref RegisterBC.High);
                    break;
                case 0x05: DecrementRegister8Bit(ref RegisterBC.High);
                    break;
                case 0x06: LoadValueToRegister8Bit(ref RegisterBC.High); 
                    break;
                case 0x07: RotateALeftNoCarry();
                    break;
                case 0x08: LoadRegisterToMemory(StackPointer);
                    break;
                case 0x09: AddValueToRegisterHL(RegisterBC);
                    break;
                case 0x0A: LoadMemoryToRegister8Bit(ref RegisterAF.High, RegisterBC.Value);
                    break;
                case 0x0B: DecrementRegister16Bit(RegisterBC);
                    break;
                case 0x0C: IncrementRegister8Bit(ref RegisterBC.Low);
                    break;
                case 0x0D: DecrementRegister8Bit(ref RegisterBC.Low);
                    break;
                case 0x0E: LoadValueToRegister8Bit(ref RegisterBC.Low); 
                    break;
                case 0x0F: RotateARightNoCarry();
                    break;
                case 0x10: Stopped = true;
                    break;
                case 0x11: LoadValueToRegister16Bit(RegisterDE);
                    break;
                case 0x12: LoadValueToMemory8Bit(RegisterDE.Value, RegisterAF.High);
                    break;
                case 0x13: IncrementRegister16Bit(RegisterDE);
                    break;
                case 0x14: IncrementRegister8Bit(ref RegisterDE.High);
                    break;
                case 0x15: DecrementRegister8Bit(ref RegisterDE.High);
                    break;
                case 0x16: LoadValueToRegister8Bit(ref RegisterDE.High); 
                    break;
                case 0x17: RotateALeftThroughCarry();
                    break;
                case 0x18: Jump((sbyte)ReadNextValue());
                    break;
                case 0x19: AddValueToRegisterHL(RegisterDE);
                    break;
                case 0x1A: LoadMemoryToRegister8Bit(ref RegisterAF.High, RegisterDE.Value);
                    break;
                case 0x1B: DecrementRegister16Bit(RegisterDE);
                    break;
                case 0x1C: IncrementRegister8Bit(ref RegisterDE.Low);
                    break;
                case 0x1D: DecrementRegister8Bit(ref RegisterDE.Low);
                    break;
                case 0x1E: LoadValueToRegister8Bit(ref RegisterDE.Low); 
                    break;
                case 0x1F: RotateARightThroughCarry();
                    break;
                case 0x20: ConditionallyJump(!IsFlagSet(FlagZ), (sbyte)ReadNextValue());
                    break;
                case 0x21: LoadValueToRegister16Bit(RegisterHL);
                    break;
                case 0x22: LoadValueToMemory8Bit(RegisterHL.Value, RegisterAF.High); RegisterHL.Value++;
                    break;
                case 0x23: IncrementRegister16Bit(RegisterHL);
                    break;
                case 0x24: IncrementRegister8Bit(ref RegisterHL.High);
                    break;
                case 0x25: DecrementRegister8Bit(ref RegisterHL.High);
                    break;
                case 0x26: LoadValueToRegister8Bit(ref RegisterHL.High); 
                    break;
                case 0x27: DecimalAdjustRegisterA();
                    break;
                case 0x28: ConditionallyJump(IsFlagSet(FlagZ), (sbyte)ReadNextValue());
                    break;
                case 0x29: AddValueToRegisterHL(RegisterHL);
                    break;
                case 0x2A: LoadMemoryToRegister8Bit(ref RegisterAF.High, RegisterHL.Value); RegisterHL.Value++;
                    break;
                case 0x2B: DecrementRegister16Bit(RegisterHL);
                    break;
                case 0x2C: IncrementRegister8Bit(ref RegisterHL.Low);
                    break;
                case 0x2D: DecrementRegister8Bit(ref RegisterHL.Low);
                    break;
                case 0x2E: LoadValueToRegister8Bit(ref RegisterHL.Low); 
                    break;
                case 0x2F: ComplementRegisterA();
                    break;
                case 0x30: ConditionallyJump(!IsFlagSet(FlagC), (sbyte)ReadNextValue());
                    break;
                case 0x31: LoadValueToRegister16Bit(StackPointer);
                    break;
                case 0x32: LoadValueToMemory8Bit(RegisterHL.Value, RegisterAF.High); RegisterHL.Value--;
                    break;
                case 0x33: IncrementRegister16Bit(StackPointer);
                    break;
                case 0x34: IncrementMemory(RegisterHL.Value);
                    break;
                case 0x35: DecrementMemory(RegisterHL.Value);
                    break;
                case 0x36: LoadValueToMemory8Bit(RegisterHL.Value, ReadNextValue());
                    break;
                case 0x37: SetCarryFlag();
                    break;
                case 0x38: ConditionallyJump(IsFlagSet(FlagC), (sbyte)ReadNextValue());
                    break;
                case 0x39: AddValueToRegisterHL(StackPointer);
                    break;
                case 0x3A: LoadMemoryToRegister8Bit(ref RegisterAF.High, RegisterHL.Value); RegisterHL.Value--;
                    break;
                case 0x3B: DecrementRegister16Bit(StackPointer);
                    break;
                case 0x3C: IncrementRegister8Bit(ref RegisterAF.High);
                    break;
                case 0x3D: DecrementRegister8Bit(ref RegisterAF.High);
                    break;
                case 0x3E: LoadValueToRegister8Bit(ref RegisterAF.High);
                    break;
                case 0x3F: ComplementCarryFlag();
                    break;
                case 0x40: LoadRegisterToRegister8Bit(ref RegisterBC.High, RegisterBC.High);
                    break;
                case 0x41: LoadRegisterToRegister8Bit(ref RegisterBC.High, RegisterBC.Low); 
                    break;
                case 0x42: LoadRegisterToRegister8Bit(ref RegisterBC.High, RegisterDE.High); 
                    break;
                case 0x43: LoadRegisterToRegister8Bit(ref RegisterBC.High, RegisterDE.Low);
                    break;
                case 0x44: LoadRegisterToRegister8Bit(ref RegisterBC.High, RegisterHL.High);
                    break;
                case 0x45: LoadRegisterToRegister8Bit(ref RegisterBC.High, RegisterHL.Low); 
                    break;
                case 0x46: LoadMemoryToRegister8Bit(ref RegisterBC.High, RegisterHL.Value);
                    break;
                case 0x47: LoadRegisterToRegister8Bit(ref RegisterBC.High, RegisterAF.High);
                    break;
                case 0x48: LoadRegisterToRegister8Bit(ref RegisterBC.Low, RegisterBC.High);
                    break;
                case 0x49: LoadRegisterToRegister8Bit(ref RegisterBC.Low, RegisterBC.Low);
                    break;
                case 0x4A: LoadRegisterToRegister8Bit(ref RegisterBC.Low, RegisterDE.High);
                    break;
                case 0x4B: LoadRegisterToRegister8Bit(ref RegisterBC.Low, RegisterDE.Low);
                    break;
                case 0x4C: LoadRegisterToRegister8Bit(ref RegisterBC.Low, RegisterHL.High);
                    break;
                case 0x4D: LoadRegisterToRegister8Bit(ref RegisterBC.Low, RegisterHL.Low);
                    break;
                case 0x4E: LoadMemoryToRegister8Bit(ref RegisterBC.Low, RegisterHL.Value);
                    break;
                case 0x4F: LoadRegisterToRegister8Bit(ref RegisterBC.Low, RegisterAF.High);
                    break;
                case 0x50: LoadRegisterToRegister8Bit(ref RegisterDE.High, RegisterBC.High);
                    break;
                case 0x51: LoadRegisterToRegister8Bit(ref RegisterDE.High, RegisterBC.Low);
                    break;
                case 0x52: LoadRegisterToRegister8Bit(ref RegisterDE.High, RegisterDE.High);
                    break;
                case 0x53: LoadRegisterToRegister8Bit(ref RegisterDE.High, RegisterDE.Low);
                    break;
                case 0x54: LoadRegisterToRegister8Bit(ref RegisterDE.High, RegisterHL.High);
                    break;
                case 0x55: LoadRegisterToRegister8Bit(ref RegisterDE.High, RegisterHL.Low);
                    break;
                case 0x56: LoadMemoryToRegister8Bit(ref RegisterDE.High, RegisterHL.Value);
                    break;
                case 0x57: LoadRegisterToRegister8Bit(ref RegisterDE.High, RegisterAF.High);
                    break;
                case 0x58: LoadRegisterToRegister8Bit(ref RegisterDE.Low, RegisterBC.High);
                    break;
                case 0x59: LoadRegisterToRegister8Bit(ref RegisterDE.Low, RegisterBC.Low);
                    break;
                case 0x5A: LoadRegisterToRegister8Bit(ref RegisterDE.Low, RegisterDE.High);
                    break;
                case 0x5B: LoadRegisterToRegister8Bit(ref RegisterDE.Low, RegisterDE.Low);
                    break;
                case 0x5C: LoadRegisterToRegister8Bit(ref RegisterDE.Low, RegisterHL.High);
                    break;
                case 0x5D: LoadRegisterToRegister8Bit(ref RegisterDE.Low, RegisterHL.Low);
                    break;
                case 0x5E: LoadMemoryToRegister8Bit(ref RegisterDE.Low, RegisterHL.Value);
                    break;
                case 0x5F: LoadRegisterToRegister8Bit(ref RegisterDE.Low, RegisterAF.High);
                    break;
                case 0x60: LoadRegisterToRegister8Bit(ref RegisterHL.High, RegisterBC.High);
                    break;
                case 0x61: LoadRegisterToRegister8Bit(ref RegisterHL.High, RegisterBC.Low);
                    break;
                case 0x62: LoadRegisterToRegister8Bit(ref RegisterHL.High, RegisterDE.High);
                    break;
                case 0x63: LoadRegisterToRegister8Bit(ref RegisterHL.High, RegisterDE.Low);
                    break;
                case 0x64: LoadRegisterToRegister8Bit(ref RegisterHL.High, RegisterHL.High);
                    break;
                case 0x65: LoadRegisterToRegister8Bit(ref RegisterHL.High, RegisterHL.Low);
                    break;
                case 0x66: LoadMemoryToRegister8Bit(ref RegisterHL.High, RegisterHL.Value);
                    break;
                case 0x67: LoadRegisterToRegister8Bit(ref RegisterHL.High, RegisterAF.High);
                    break;
                case 0x68: LoadRegisterToRegister8Bit(ref RegisterHL.Low, RegisterBC.High);
                    break;
                case 0x69: LoadRegisterToRegister8Bit(ref RegisterHL.Low, RegisterBC.Low);
                    break;
                case 0x6A: LoadRegisterToRegister8Bit(ref RegisterHL.Low, RegisterDE.High);
                    break;
                case 0x6B: LoadRegisterToRegister8Bit(ref RegisterHL.Low, RegisterDE.Low);
                    break;
                case 0x6C: LoadRegisterToRegister8Bit(ref RegisterHL.Low, RegisterHL.High);
                    break;
                case 0x6D: LoadRegisterToRegister8Bit(ref RegisterHL.Low, RegisterHL.Low);
                    break;
                case 0x6E: LoadMemoryToRegister8Bit(ref RegisterHL.Low, RegisterHL.Value);
                    break;
                case 0x6F: LoadRegisterToRegister8Bit(ref RegisterHL.Low, RegisterAF.High);
                    break;
                case 0x70: LoadValueToMemory8Bit(RegisterHL.Value, RegisterBC.High);
                    break;                             
                case 0x71: LoadValueToMemory8Bit(RegisterHL.Value, RegisterBC.Low);
                    break;                             
                case 0x72: LoadValueToMemory8Bit(RegisterHL.Value, RegisterDE.High);
                    break;                             
                case 0x73: LoadValueToMemory8Bit(RegisterHL.Value, RegisterDE.Low);
                    break;                             
                case 0x74: LoadValueToMemory8Bit(RegisterHL.Value, RegisterHL.High);
                    break;                             
                case 0x75: LoadValueToMemory8Bit(RegisterHL.Value, RegisterHL.Low);
                    break;
                case 0x76: Halted = true;
                    break;
                case 0x77: LoadValueToMemory8Bit(RegisterHL.Value, RegisterAF.High);
                    break;
                case 0x78: LoadRegisterToRegister8Bit(ref RegisterAF.High, RegisterBC.High); 
                    break;
                case 0x79: LoadRegisterToRegister8Bit(ref RegisterAF.High, RegisterBC.Low); 
                    break;
                case 0x7A: LoadRegisterToRegister8Bit(ref RegisterAF.High, RegisterDE.High); 
                    break;
                case 0x7B: LoadRegisterToRegister8Bit(ref RegisterAF.High, RegisterDE.Low); 
                    break;
                case 0x7C: LoadRegisterToRegister8Bit(ref RegisterAF.High, RegisterHL.High); 
                    break;
                case 0x7D: LoadRegisterToRegister8Bit(ref RegisterAF.High, RegisterHL.Low); 
                    break;
                case 0x7E: LoadMemoryToRegister8Bit(ref RegisterAF.High, RegisterHL.Value);
                    break;
                case 0x7F: LoadRegisterToRegister8Bit(ref RegisterAF.High, RegisterAF.High); 
                    break;
                case 0x80: AddValueToRegisterA(RegisterBC.High);
                    break;
                case 0x81: AddValueToRegisterA(RegisterBC.Low);
                    break;
                case 0x82: AddValueToRegisterA(RegisterDE.High);
                    break;
                case 0x83: AddValueToRegisterA(RegisterDE.Low);
                    break;
                case 0x84: AddValueToRegisterA(RegisterHL.High);
                    break;
                case 0x85: AddValueToRegisterA(RegisterHL.Low);
                    break;
                case 0x86: AddValueToRegisterA(Memory[RegisterHL.Value]);
                    break;
                case 0x87: AddValueToRegisterA(RegisterAF.High);
                    break;
                case 0x88: AddValueToRegisterA(RegisterBC.High, true);
                    break;
                case 0x89: AddValueToRegisterA(RegisterBC.Low, true);
                    break;
                case 0x8A: AddValueToRegisterA(RegisterDE.High, true);
                    break;
                case 0x8B: AddValueToRegisterA(RegisterDE.Low, true);
                    break;
                case 0x8C: AddValueToRegisterA(RegisterHL.High, true);
                    break;
                case 0x8D: AddValueToRegisterA(RegisterHL.Low, true);
                    break;
                case 0x8E: AddValueToRegisterA(Memory[RegisterHL.Value], true);
                    break;
                case 0x8F: AddValueToRegisterA(RegisterAF.High, true);
                    break;
                case 0x90: SubtractValueFromRegisterA(RegisterBC.High);
                    break;
                case 0x91: SubtractValueFromRegisterA(RegisterBC.Low);
                    break;
                case 0x92: SubtractValueFromRegisterA(RegisterDE.High);
                    break;
                case 0x93: SubtractValueFromRegisterA(RegisterDE.Low);
                    break;
                case 0x94: SubtractValueFromRegisterA(RegisterHL.High);
                    break;
                case 0x95: SubtractValueFromRegisterA(RegisterHL.Low);
                    break;
                case 0x96: SubtractValueFromRegisterA(Memory[RegisterHL.Value]);
                    break;
                case 0x97: SubtractValueFromRegisterA(RegisterAF.High);
                    break;
                case 0x98: SubtractValueFromRegisterA(RegisterBC.High, true);
                    break;
                case 0x99: SubtractValueFromRegisterA(RegisterBC.Low, true);
                    break;
                case 0x9A: SubtractValueFromRegisterA(RegisterDE.High, true);
                    break;
                case 0x9B: SubtractValueFromRegisterA(RegisterDE.Low, true);
                    break;
                case 0x9C: SubtractValueFromRegisterA(RegisterHL.High, true);
                    break;
                case 0x9D: SubtractValueFromRegisterA(RegisterHL.Low, true);
                    break;
                case 0x9E: SubtractValueFromRegisterA(Memory[RegisterHL.Value], true);
                    break;
                case 0x9F: SubtractValueFromRegisterA(RegisterAF.High, true);
                    break;
                case 0xA0: AndWithRegisterA(RegisterBC.High);
                    break;
                case 0xA1: AndWithRegisterA(RegisterBC.Low);
                    break;
                case 0xA2: AndWithRegisterA(RegisterDE.High);
                    break;
                case 0xA3: AndWithRegisterA(RegisterDE.Low);
                    break;
                case 0xA4: AndWithRegisterA(RegisterHL.High);
                    break;
                case 0xA5: AndWithRegisterA(RegisterHL.Low);
                    break;
                case 0xA6: AndWithRegisterA(Memory[RegisterHL.Value]);
                    break;
                case 0xA7: AndWithRegisterA(RegisterAF.High);
                    break;
                case 0xA8: XorWithRegisterA(RegisterBC.High);
                    break;
                case 0xA9: XorWithRegisterA(RegisterBC.Low);
                    break;
                case 0xAA: XorWithRegisterA(RegisterDE.High);
                    break;
                case 0xAB: XorWithRegisterA(RegisterDE.Low);
                    break;
                case 0xAC: XorWithRegisterA(RegisterHL.High);
                    break;
                case 0xAD: XorWithRegisterA(RegisterHL.Low);
                    break;
                case 0xAE: XorWithRegisterA(Memory[RegisterHL.Value]);
                    break;
                case 0xAF: XorWithRegisterA(RegisterAF.High);
                    break;
                case 0xB0: OrWithRegisterA(RegisterBC.High);
                    break;
                case 0xB1: OrWithRegisterA(RegisterBC.Low);
                    break;
                case 0xB2: OrWithRegisterA(RegisterDE.High);
                    break;
                case 0xB3: OrWithRegisterA(RegisterDE.Low);
                    break;
                case 0xB4: OrWithRegisterA(RegisterHL.High);
                    break;
                case 0xB5: OrWithRegisterA(RegisterHL.Low);
                    break;
                case 0xB6: OrWithRegisterA(Memory[RegisterHL.Value]);
                    break;
                case 0xB7: OrWithRegisterA(RegisterAF.High);
                    break;
                case 0xB8: CompareWithRegisterA(RegisterBC.High);
                    break;
                case 0xB9: CompareWithRegisterA(RegisterBC.Low);
                    break;
                case 0xBA: CompareWithRegisterA(RegisterDE.High);
                    break;
                case 0xBB: CompareWithRegisterA(RegisterDE.Low);
                    break;
                case 0xBC: CompareWithRegisterA(RegisterHL.High);
                    break;
                case 0xBD: CompareWithRegisterA(RegisterHL.Low);
                    break;
                case 0xBE: CompareWithRegisterA(Memory[RegisterHL.Value]);
                    break;
                case 0xBF: CompareWithRegisterA(RegisterAF.High);
                    break;
                case 0xC0: ConditionallyReturn(!IsFlagSet(FlagZ));
                    break;
                case 0xC1: PopValuesIntoRegister(RegisterBC);
                    break;
                case 0xC2: ConditionallyJump(!IsFlagSet(FlagZ), ReadNextTwoValues());
                    break;
                case 0xC3: Jump(ReadNextTwoValues());
                    break;
                case 0xC4: ConditionallyCall(!IsFlagSet(FlagZ), ReadNextTwoValues());
                    break;
                case 0xC5: PushAddressOntoStack(RegisterBC.Value);
                    break;
                case 0xC6: AddValueToRegisterA(ReadNextValue());
                    break;
                case 0xC7: Restart(0x00);
                    break;
                case 0xC8: ConditionallyReturn(IsFlagSet(FlagZ));
                    break;
                case 0xC9: Return();
                    break;
                case 0xCA: ConditionallyJump(IsFlagSet(FlagZ), ReadNextTwoValues());
                    break;
                case 0xCB: // This should always be handled as an extended op code.
                    break;
                case 0xCC: ConditionallyCall(IsFlagSet(FlagZ), ReadNextTwoValues());
                    break;
                case 0xCD: Call(ReadNextTwoValues());
                    break;
                case 0xCE: AddValueToRegisterA(ReadNextValue(), true);
                    break;
                case 0xCF: Restart(0x08);
                    break;
                case 0xD0: ConditionallyReturn(!IsFlagSet(FlagC));
                    break;
                case 0xD1: PopValuesIntoRegister(RegisterDE);
                    break;
                case 0xD2: ConditionallyJump(!IsFlagSet(FlagC), ReadNextTwoValues());
                    break;
                case 0xD4: ConditionallyCall(!IsFlagSet(FlagC), ReadNextTwoValues());
                    break;
                case 0xD5: PushAddressOntoStack(RegisterDE.Value);
                    break;
                case 0xD6: SubtractValueFromRegisterA(ReadNextValue());
                    break;
                case 0xD7: Restart(0x10);
                    break;
                case 0xD8: ConditionallyReturn(IsFlagSet(FlagC));
                    break;
                case 0xD9: ReturnAndEnableInterrupts();
                    break;
                case 0xDA: ConditionallyJump(IsFlagSet(FlagC), ReadNextTwoValues());
                    break;
                case 0xDC: ConditionallyCall(IsFlagSet(FlagC), ReadNextTwoValues());
                    break;
                case 0xDE: SubtractValueFromRegisterA(ReadNextValue(), true);
                    break;
                case 0xDF: Restart(0x18);
                    break;
                case 0xE0: LoadValueToMemory8Bit((ushort)(0xFF00 + ReadNextValue()), RegisterAF.High);
                    break;
                case 0xE1: PopValuesIntoRegister(RegisterHL);
                    break;
                case 0xE2: LoadValueToMemory8Bit((ushort)(0xFF00 + RegisterBC.Low), RegisterAF.High);
                    break;
                case 0xE5: PushAddressOntoStack(RegisterHL.Value);
                    break;
                case 0xE6: AndWithRegisterA(ReadNextValue());
                    break;
                case 0xE7: Restart(0x20);
                    break;
                case 0xE8: AddValueToStackPointer();
                    break;
                case 0xE9: Jump(RegisterHL.Value);
                    break;
                case 0xEA: LoadValueToMemory8Bit(ReadNextTwoValues(), RegisterAF.High);
                    break;
                case 0xEE: XorWithRegisterA(ReadNextValue());
                    break;
                case 0xEF: Restart(0x28);
                    break;
                case 0xF0: LoadMemoryToRegister8Bit(ref RegisterAF.High, (ushort)(0xFF00 + ReadNextValue()));
                    break;
                case 0xF1: PopValuesIntoRegister(RegisterAF);
                    break;
                case 0xF2: LoadMemoryToRegister8Bit(ref RegisterAF.High, (ushort)(0xFF00 + RegisterBC.Low));
                    break;
                case 0xF3: InterruptsEnabled = false;
                    break;
                case 0xF5: PushAddressOntoStack(RegisterAF.Value);
                    break;
                case 0xF6: OrWithRegisterA(ReadNextValue());
                    break;
                case 0xF7: Restart(0x30);
                    break;
                case 0xF8: LoadStackPointerToRegisterHL();
                    break;
                case 0xF9: LoadRegisterToRegister16Bit(StackPointer, RegisterHL);
                    break;
                case 0xFA: LoadMemoryToRegister8Bit(ref RegisterAF.High, ReadNextTwoValues());
                    break;
                case 0xFB: InterruptsEnabled = true;
                    break;
                case 0xFE: CompareWithRegisterA(ReadNextValue());
                    break;
                case 0xFF: Restart(0x38);
                    break;                
            }
        }

        // For all op codes prefixed with 0xCB
        public void ExecuteExtendedOpCode(ushort extended)
        {
            switch (extended)
            {
                case 0xCB00: RotateLeftNoCarry(ref RegisterBC.High);
                    break;
                case 0xCB01: RotateLeftNoCarry(ref RegisterBC.Low);
                    break;
                case 0xCB02: RotateLeftNoCarry(ref RegisterDE.High);
                    break;
                case 0xCB03: RotateLeftNoCarry(ref RegisterDE.Low);
                    break;
                case 0xCB04: RotateLeftNoCarry(ref RegisterHL.High);
                    break;
                case 0xCB05: RotateLeftNoCarry(ref RegisterHL.Low);
                    break;
                case 0xCB06: RotateLeftNoCarry(RegisterHL.Value);
                    break;
                case 0xCB07: RotateLeftNoCarry(ref RegisterAF.High);
                    break;
                case 0xCB08: RotateRightNoCarry(ref RegisterBC.High);
                    break;
                case 0xCB09: RotateRightNoCarry(ref RegisterBC.Low);
                    break;
                case 0xCB0A: RotateRightNoCarry(ref RegisterDE.High);
                    break;
                case 0xCB0B: RotateRightNoCarry(ref RegisterDE.Low);
                    break;
                case 0xCB0C: RotateRightNoCarry(ref RegisterHL.High);
                    break;
                case 0xCB0D: RotateRightNoCarry(ref RegisterHL.Low);
                    break;
                case 0xCB0E: RotateRightNoCarry(RegisterHL.Value);
                    break;
                case 0xCB0F: RotateRightNoCarry(ref RegisterAF.High);
                    break;
                case 0xCB10: RotateLeftThroughCarry(ref RegisterBC.High);
                    break;
                case 0xCB11: RotateLeftThroughCarry(ref RegisterBC.Low);
                    break;
                case 0xCB12: RotateLeftThroughCarry(ref RegisterDE.High);
                    break;
                case 0xCB13: RotateLeftThroughCarry(ref RegisterDE.Low);
                    break;
                case 0xCB14: RotateLeftThroughCarry(ref RegisterHL.High);
                    break;
                case 0xCB15: RotateLeftThroughCarry(ref RegisterHL.Low);
                    break;
                case 0xCB16: RotateLeftThroughCarry(RegisterHL.Value);
                    break;
                case 0xCB17: RotateLeftThroughCarry(ref RegisterAF.High);
                    break;
                case 0xCB18: RotateRightThroughCarry(ref RegisterBC.High);
                    break;
                case 0xCB19: RotateRightThroughCarry(ref RegisterBC.Low);
                    break;
                case 0xCB1A: RotateRightThroughCarry(ref RegisterDE.High);
                    break;
                case 0xCB1B: RotateRightThroughCarry(ref RegisterDE.Low);
                    break;
                case 0xCB1C: RotateRightThroughCarry(ref RegisterHL.High);
                    break;
                case 0xCB1D: RotateRightThroughCarry(ref RegisterHL.Low);
                    break;
                case 0xCB1E: RotateRightThroughCarry(RegisterHL.Value);
                    break;
                case 0xCB1F: RotateRightThroughCarry(ref RegisterAF.High);
                    break;
                case 0xCB20: LogicalShiftLeft(ref RegisterBC.High);
                    break;
                case 0xCB21: LogicalShiftLeft(ref RegisterBC.Low);
                    break;
                case 0xCB22: LogicalShiftLeft(ref RegisterDE.High);
                    break;
                case 0xCB23: LogicalShiftLeft(ref RegisterDE.Low);
                    break;
                case 0xCB24: LogicalShiftLeft(ref RegisterHL.High);
                    break;
                case 0xCB25: LogicalShiftLeft(ref RegisterHL.Low);
                    break;
                case 0xCB26: LogicalShiftLeft(RegisterHL.Value);
                    break;
                case 0xCB27: LogicalShiftLeft(ref RegisterAF.High);
                    break;
                case 0xCB28: ArithmeticShiftRight(ref RegisterBC.High);
                    break;
                case 0xCB29: ArithmeticShiftRight(ref RegisterBC.Low);
                    break;
                case 0xCB2A: ArithmeticShiftRight(ref RegisterDE.High);
                    break;
                case 0xCB2B: ArithmeticShiftRight(ref RegisterDE.Low);
                    break;
                case 0xCB2C: ArithmeticShiftRight(ref RegisterHL.High);
                    break;
                case 0xCB2D: ArithmeticShiftRight(ref RegisterHL.Low);
                    break;
                case 0xCB2E: ArithmeticShiftRight(RegisterHL.Value);
                    break;
                case 0xCB2F: ArithmeticShiftRight(ref RegisterAF.High);
                    break;
                case 0xCB30: SwapNibbles(ref RegisterBC.High);
                    break;
                case 0xCB31: SwapNibbles(ref RegisterBC.Low);
                    break;
                case 0xCB32: SwapNibbles(ref RegisterDE.High);
                    break;
                case 0xCB33: SwapNibbles(ref RegisterDE.Low);
                    break;
                case 0xCB34: SwapNibbles(ref RegisterHL.High);
                    break;
                case 0xCB35: SwapNibbles(ref RegisterHL.Low);
                    break;
                case 0xCB36: SwapNibbles(RegisterHL.Value);
                    break;
                case 0xCB37: SwapNibbles(ref RegisterAF.High);
                    break;
                case 0xCB38: LogicalShiftRight(ref RegisterBC.High);
                    break;
                case 0xCB39: LogicalShiftRight(ref RegisterBC.Low);
                    break;
                case 0xCB3A: LogicalShiftRight(ref RegisterDE.High);
                    break;
                case 0xCB3B: LogicalShiftRight(ref RegisterDE.Low);
                    break;
                case 0xCB3C: LogicalShiftRight(ref RegisterHL.High);
                    break;
                case 0xCB3D: LogicalShiftRight(ref RegisterHL.Low);
                    break;
                case 0xCB3E: LogicalShiftRight(RegisterHL.Value);
                    break;
                case 0xCB3F: LogicalShiftRight(ref RegisterAF.High);
                    break;
                case 0xCB40: TestBit(0, RegisterBC.High);
                    break;
                case 0xCB41: TestBit(0, RegisterBC.Low);
                    break;
                case 0xCB42: TestBit(0, RegisterDE.High);
                    break;
                case 0xCB43: TestBit(0, RegisterDE.Low);
                    break;
                case 0xCB44: TestBit(0, RegisterHL.High);
                    break;
                case 0xCB45: TestBit(0, RegisterHL.Low);
                    break;
                case 0xCB46: TestBit(0, Memory[RegisterHL.Value]);
                    break;
                case 0xCB47: TestBit(0, RegisterAF.High);
                    break;
                case 0xCB48: TestBit(1, RegisterBC.High);
                    break;           
                case 0xCB49: TestBit(1, RegisterBC.Low);
                    break;           
                case 0xCB4A: TestBit(1, RegisterDE.High);
                    break;           
                case 0xCB4B: TestBit(1, RegisterDE.Low);
                    break;           
                case 0xCB4C: TestBit(1, RegisterHL.High);
                    break;           
                case 0xCB4D: TestBit(1, RegisterHL.Low);
                    break;           
                case 0xCB4E: TestBit(1, Memory[RegisterHL.Value]);
                    break;
                case 0xCB4F: TestBit(1, RegisterAF.High);
                    break;
                case 0xCB50: TestBit(2, RegisterBC.High);
                    break;           
                case 0xCB51: TestBit(2, RegisterBC.Low);
                    break;           
                case 0xCB52: TestBit(2, RegisterDE.High);
                    break;           
                case 0xCB53: TestBit(2, RegisterDE.Low);
                    break;           
                case 0xCB54: TestBit(2, RegisterHL.High);
                    break;           
                case 0xCB55: TestBit(2, RegisterHL.Low);
                    break;           
                case 0xCB56: TestBit(2, Memory[RegisterHL.Value]);
                    break;           
                case 0xCB57: TestBit(2, RegisterAF.High);
                    break;
                case 0xCB58: TestBit(3, RegisterBC.High);
                    break;           
                case 0xCB59: TestBit(3, RegisterBC.Low);
                    break;           
                case 0xCB5A: TestBit(3, RegisterDE.High);
                    break;           
                case 0xCB5B: TestBit(3, RegisterDE.Low);
                    break;           
                case 0xCB5C: TestBit(3, RegisterHL.High);
                    break;           
                case 0xCB5D: TestBit(3, RegisterHL.Low);
                    break;           
                case 0xCB5E: TestBit(3, Memory[RegisterHL.Value]);
                    break;           
                case 0xCB5F: TestBit(3, RegisterAF.High);
                    break;
                case 0xCB60: TestBit(4, RegisterBC.High);
                    break;           
                case 0xCB61: TestBit(4, RegisterBC.Low);
                    break;           
                case 0xCB62: TestBit(4, RegisterDE.High);
                    break;           
                case 0xCB63: TestBit(4, RegisterDE.Low);
                    break;           
                case 0xCB64: TestBit(4, RegisterHL.High);
                    break;           
                case 0xCB65: TestBit(4, RegisterHL.Low);
                    break;           
                case 0xCB66: TestBit(4, Memory[RegisterHL.Value]);
                    break;           
                case 0xCB67: TestBit(4, RegisterAF.High);
                    break;
                case 0xCB68: TestBit(5, RegisterBC.High);
                    break;           
                case 0xCB69: TestBit(5, RegisterBC.Low);
                    break;           
                case 0xCB6A: TestBit(5, RegisterDE.High);
                    break;           
                case 0xCB6B: TestBit(5, RegisterDE.Low);
                    break;           
                case 0xCB6C: TestBit(5, RegisterHL.High);
                    break;           
                case 0xCB6D: TestBit(5, RegisterHL.Low);
                    break;           
                case 0xCB6E: TestBit(5, Memory[RegisterHL.Value]);
                    break;           
                case 0xCB6F: TestBit(5, RegisterAF.High);
                    break;
                case 0xCB70: TestBit(6, RegisterBC.High);
                    break;           
                case 0xCB71: TestBit(6, RegisterBC.Low);
                    break;           
                case 0xCB72: TestBit(6, RegisterDE.High);
                    break;           
                case 0xCB73: TestBit(6, RegisterDE.Low);
                    break;           
                case 0xCB74: TestBit(6, RegisterHL.High);
                    break;           
                case 0xCB75: TestBit(6, RegisterHL.Low);
                    break;           
                case 0xCB76: TestBit(6, Memory[RegisterHL.Value]);
                    break;           
                case 0xCB77: TestBit(6, RegisterAF.High);
                    break;
                case 0xCB78: TestBit(7, RegisterBC.High);
                    break;           
                case 0xCB79: TestBit(7, RegisterBC.Low);
                    break;           
                case 0xCB7A: TestBit(7, RegisterDE.High);
                    break;           
                case 0xCB7B: TestBit(7, RegisterDE.Low);
                    break;           
                case 0xCB7C: TestBit(7, RegisterHL.High);
                    break;           
                case 0xCB7D: TestBit(7, RegisterHL.Low);
                    break;           
                case 0xCB7E: TestBit(7, Memory[RegisterHL.Value]);
                    break;           
                case 0xCB7F: TestBit(7, RegisterAF.High);
                    break;
                case 0xCB80: ResetBit(0, ref RegisterBC.High);
                    break;
                case 0xCB81: ResetBit(0, ref RegisterBC.Low);
                    break;
                case 0xCB82: ResetBit(0, ref RegisterDE.High);
                    break;
                case 0xCB83: ResetBit(0, ref RegisterDE.Low);
                    break;
                case 0xCB84: ResetBit(0, ref RegisterHL.High);
                    break;
                case 0xCB85: ResetBit(0, ref RegisterHL.Low);
                    break;
                case 0xCB86: ResetBit(0, RegisterHL.Value);
                    break;
                case 0xCB87: ResetBit(0, ref RegisterAF.High);
                    break;
                case 0xCB88: ResetBit(1, ref RegisterBC.High);
                    break;            
                case 0xCB89: ResetBit(1, ref RegisterBC.Low);
                    break;            
                case 0xCB8A: ResetBit(1, ref RegisterDE.High);
                    break;            
                case 0xCB8B: ResetBit(1, ref RegisterDE.Low);
                    break;            
                case 0xCB8C: ResetBit(1, ref RegisterHL.High);
                    break;            
                case 0xCB8D: ResetBit(1, ref RegisterHL.Low);
                    break;            
                case 0xCB8E: ResetBit(1, RegisterHL.Value);
                    break;            
                case 0xCB8F: ResetBit(1, ref RegisterAF.High);
                    break;
                case 0xCB90: ResetBit(2, ref RegisterBC.High);
                    break;            
                case 0xCB91: ResetBit(2, ref RegisterBC.Low);
                    break;            
                case 0xCB92: ResetBit(2, ref RegisterDE.High);
                    break;            
                case 0xCB93: ResetBit(2, ref RegisterDE.Low);
                    break;            
                case 0xCB94: ResetBit(2, ref RegisterHL.High);
                    break;            
                case 0xCB95: ResetBit(2, ref RegisterHL.Low);
                    break;            
                case 0xCB96: ResetBit(2, RegisterHL.Value);
                    break;            
                case 0xCB97: ResetBit(2, ref RegisterAF.High);
                    break;
                case 0xCB98: ResetBit(3, ref RegisterBC.High);
                    break;            
                case 0xCB99: ResetBit(3, ref RegisterBC.Low);
                    break;            
                case 0xCB9A: ResetBit(3, ref RegisterDE.High);
                    break;            
                case 0xCB9B: ResetBit(3, ref RegisterDE.Low);
                    break;            
                case 0xCB9C: ResetBit(3, ref RegisterHL.High);
                    break;            
                case 0xCB9D: ResetBit(3, ref RegisterHL.Low);
                    break;            
                case 0xCB9E: ResetBit(3, RegisterHL.Value);
                    break;            
                case 0xCB9F: ResetBit(3, ref RegisterAF.High);
                    break;
                case 0xCBA0: ResetBit(4, ref RegisterBC.High);
                    break;            
                case 0xCBA1: ResetBit(4, ref RegisterBC.Low);
                    break;            
                case 0xCBA2: ResetBit(4, ref RegisterDE.High);
                    break;            
                case 0xCBA3: ResetBit(4, ref RegisterDE.Low);
                    break;            
                case 0xCBA4: ResetBit(4, ref RegisterHL.High);
                    break;            
                case 0xCBA5: ResetBit(4, ref RegisterHL.Low);
                    break;            
                case 0xCBA6: ResetBit(4, RegisterHL.Value);
                    break;            
                case 0xCBA7: ResetBit(4, ref RegisterAF.High);
                    break;
                case 0xCBA8: ResetBit(5, ref RegisterBC.High);
                    break;            
                case 0xCBA9: ResetBit(5, ref RegisterBC.Low);
                    break;            
                case 0xCBAA: ResetBit(5, ref RegisterDE.High);
                    break;            
                case 0xCBAB: ResetBit(5, ref RegisterDE.Low);
                    break;            
                case 0xCBAC: ResetBit(5, ref RegisterHL.High);
                    break;            
                case 0xCBAD: ResetBit(5, ref RegisterHL.Low);
                    break;            
                case 0xCBAE: ResetBit(5, RegisterHL.Value);
                    break;            
                case 0xCBAF: ResetBit(5, ref RegisterAF.High);
                    break;
                case 0xCBB0: ResetBit(6, ref RegisterBC.High);
                    break;            
                case 0xCBB1: ResetBit(6, ref RegisterBC.Low);
                    break;            
                case 0xCBB2: ResetBit(6, ref RegisterDE.High);
                    break;            
                case 0xCBB3: ResetBit(6, ref RegisterDE.Low);
                    break;            
                case 0xCBB4: ResetBit(6, ref RegisterHL.High);
                    break;            
                case 0xCBB5: ResetBit(6, ref RegisterHL.Low);
                    break;            
                case 0xCBB6: ResetBit(6, RegisterHL.Value);
                    break;            
                case 0xCBB7: ResetBit(6, ref RegisterAF.High);
                    break;
                case 0xCBB8: ResetBit(7, ref RegisterBC.High);
                    break;            
                case 0xCBB9: ResetBit(7, ref RegisterBC.Low);
                    break;            
                case 0xCBBA: ResetBit(7, ref RegisterDE.High);
                    break;            
                case 0xCBBB: ResetBit(7, ref RegisterDE.Low);
                    break;            
                case 0xCBBC: ResetBit(7, ref RegisterHL.High);
                    break;            
                case 0xCBBD: ResetBit(7, ref RegisterHL.Low);
                    break;            
                case 0xCBBE: ResetBit(7, RegisterHL.Value);
                    break;            
                case 0xCBBF: ResetBit(7, ref RegisterAF.High);
                    break;
                case 0xCBC0: SetBit(0, ref RegisterBC.High);
                    break;
                case 0xCBC1: SetBit(0, ref RegisterBC.Low);
                    break;
                case 0xCBC2: SetBit(0, ref RegisterDE.High);
                    break;
                case 0xCBC3: SetBit(0, ref RegisterDE.Low);
                    break;
                case 0xCBC4: SetBit(0, ref RegisterHL.High);
                    break;
                case 0xCBC5: SetBit(0, ref RegisterHL.Low);
                    break;
                case 0xCBC6: SetBit(0, RegisterHL.Value);
                    break;
                case 0xCBC7: SetBit(0, ref RegisterAF.High);
                    break;
                case 0xCBC8: SetBit(1, ref RegisterBC.High);
                    break;
                case 0xCBC9: SetBit(1, ref RegisterBC.Low);
                    break;
                case 0xCBCA: SetBit(1, ref RegisterDE.High);
                    break;
                case 0xCBCB: SetBit(1, ref RegisterDE.Low);
                    break;
                case 0xCBCC: SetBit(1, ref RegisterHL.High);
                    break;
                case 0xCBCD: SetBit(1, ref RegisterHL.Low);
                    break;
                case 0xCBCE: SetBit(1, RegisterHL.Value);
                    break;
                case 0xCBCF: SetBit(1, ref RegisterAF.High);
                    break;
                case 0xCBD0: SetBit(2, ref RegisterBC.High);
                    break;
                case 0xCBD1: SetBit(2, ref RegisterBC.Low);
                    break;
                case 0xCBD2: SetBit(2, ref RegisterDE.High);
                    break;
                case 0xCBD3: SetBit(2, ref RegisterDE.Low);
                    break;
                case 0xCBD4: SetBit(2, ref RegisterHL.High);
                    break;
                case 0xCBD5: SetBit(2, ref RegisterHL.Low);
                    break;
                case 0xCBD6: SetBit(2, RegisterHL.Value);
                    break;
                case 0xCBD7: SetBit(2, ref RegisterAF.High);
                    break;
                case 0xCBD8: SetBit(3, ref RegisterBC.High);
                    break;
                case 0xCBD9: SetBit(3, ref RegisterBC.Low);
                    break;
                case 0xCBDA: SetBit(3, ref RegisterDE.High);
                    break;
                case 0xCBDB: SetBit(3, ref RegisterDE.Low);
                    break;
                case 0xCBDC: SetBit(3, ref RegisterHL.High);
                    break;
                case 0xCBDD: SetBit(3, ref RegisterHL.Low);
                    break;
                case 0xCBDE: SetBit(3, RegisterHL.Value);
                    break;
                case 0xCBDF: SetBit(3, ref RegisterAF.High);
                    break;
                case 0xCBE0: SetBit(4, ref RegisterBC.High);
                    break;
                case 0xCBE1: SetBit(4, ref RegisterBC.Low);
                    break;
                case 0xCBE2: SetBit(4, ref RegisterDE.High);
                    break;
                case 0xCBE3: SetBit(4, ref RegisterDE.Low);
                    break;
                case 0xCBE4: SetBit(4, ref RegisterHL.High);
                    break;
                case 0xCBE5: SetBit(4, ref RegisterHL.Low);
                    break;
                case 0xCBE6: SetBit(4, RegisterHL.Value);
                    break;
                case 0xCBE7: SetBit(4, ref RegisterAF.High);
                    break;
                case 0xCBE8: SetBit(5, ref RegisterBC.High);
                    break;
                case 0xCBE9: SetBit(5, ref RegisterBC.Low);
                    break;
                case 0xCBEA: SetBit(5, ref RegisterDE.High);
                    break;
                case 0xCBEB: SetBit(5, ref RegisterDE.Low);
                    break;
                case 0xCBEC: SetBit(5, ref RegisterHL.High);
                    break;
                case 0xCBED: SetBit(5, ref RegisterHL.Low);
                    break;
                case 0xCBEE: SetBit(5, RegisterHL.Value);
                    break;
                case 0xCBEF: SetBit(5, ref RegisterAF.High);
                    break;
                case 0xCBF0: SetBit(6, ref RegisterBC.High);
                    break;
                case 0xCBF1: SetBit(6, ref RegisterBC.Low);
                    break;
                case 0xCBF2: SetBit(6, ref RegisterDE.High);
                    break;
                case 0xCBF3: SetBit(6, ref RegisterDE.Low);
                    break;
                case 0xCBF4: SetBit(6, ref RegisterHL.High);
                    break;
                case 0xCBF5: SetBit(6, ref RegisterHL.Low);
                    break;
                case 0xCBF6: SetBit(6, RegisterHL.Value);
                    break;
                case 0xCBF7: SetBit(6, ref RegisterAF.High);
                    break;
                case 0xCBF8: SetBit(7, ref RegisterBC.High);
                    break;
                case 0xCBF9: SetBit(7, ref RegisterBC.Low);
                    break;
                case 0xCBFA: SetBit(7, ref RegisterDE.High);
                    break;
                case 0xCBFB: SetBit(7, ref RegisterDE.Low);
                    break;
                case 0xCBFC: SetBit(7, ref RegisterHL.High);
                    break;
                case 0xCBFD: SetBit(7, ref RegisterHL.Low);
                    break;
                case 0xCBFE: SetBit(7, RegisterHL.Value);
                    break;
                case 0xCBFF: SetBit(7, ref RegisterAF.High);
                    break;
            }
        }

        // Power-up sequence, reset registers and memory
        // http://problemkaputt.de/pandocs.htm#powerupsequence
        private void Reset()
        {
            RegisterAF = new Register();
            RegisterBC = new Register();
            RegisterDE = new Register();
            RegisterHL = new Register();
            StackPointer = new Register();
            ProgramCounter = 0;
            Halted = false;
            Stopped = false;
            Interrupts = new Interrupts();
            InterruptsEnabled = false;
            TimerEnabled = false;
            TimerCycleIncrement = 0;
            TimerCycles = 0;
            DividerCycles = 0;
            ScanlineCounter = 456;  // 456 cycles needed for each scanline

            int cyclesPerSecond = Properties.Settings.Default.cyclesPerSecond;
            // The divider increments at rate of 16384Hz
            DividerCycleIncrement = cyclesPerSecond / 16384;

            RegisterAF.Value = 0x01B0;
            RegisterBC.Value = 0x0013;
            RegisterDE.Value = 0x00D8;
            RegisterHL.Value = 0x014D;
            
            StackPointer.Value = 0xFFFE;
            ProgramCounter = 0x100;

            Memory.Reset();
        }

        private MemoryBankController CreateMBC(CartType cartType, Stream fileStream)
        {
            MemoryBankController mbc = null;

            switch (cartType)
            {
                case CartType.RomOnly:
                    mbc = new ROMOnly(fileStream);
                    break;
                case CartType.MBC1:
                    mbc = new MBC1(fileStream);
                    break;
                case CartType.MBC2:
                    mbc = new MBC2(fileStream);
                    break;
            }

            return mbc;
        }        

        // TODO: Re-evaluate this after core op codes have been implemented.
        // Maps the cycle count that each op code takes.
        public readonly Dictionary<int, int> CycleMap = new Dictionary<int, int>()
        {
            { 0x00,    4  }, { 0x01,    12 }, { 0x02,    8  }, { 0x03,    8  }, { 0x04,    4  }, 
            { 0x05,    4  }, { 0x06,    8  }, { 0x07,    4  }, { 0x08,    20 }, { 0x09,    8  }, 
            { 0x0A,    8  }, { 0x0B,    8  }, { 0x0C,    4  }, { 0x0D,    4  }, { 0x0E,    8  }, 
            { 0x0F,    4  }, { 0x10,    4  }, { 0x11,    12 }, { 0x12,    8  }, { 0x13,    8  }, 
            { 0x14,    4  }, { 0x15,    4  }, { 0x16,    8  }, { 0x17,    4  }, { 0x18,    8  }, 
            { 0x19,    8  }, { 0x1A,    8  }, { 0x1B,    8  }, { 0x1C,    4  }, { 0x1D,    4  }, 
            { 0x1E,    8  }, { 0x1F,    4  }, { 0x20,    8  }, { 0x21,    12 }, { 0x22,    8  }, 
            { 0x23,    8  }, { 0x24,    4  }, { 0x25,    4  }, { 0x26,    8  }, { 0x27,    4  }, 
            { 0x28,    8  }, { 0x29,    8  }, { 0x2A,    8  }, { 0x2B,    8  }, { 0x2C,    4  }, 
            { 0x2D,    4  }, { 0x2E,    8  }, { 0x2F,    4  }, { 0x30,    8  }, { 0x31,    12 }, 
            { 0x32,    8  }, { 0x33,    8  }, { 0x34,    12 }, { 0x35,    12 }, { 0x36,    12 }, 
            { 0x37,    4  }, { 0x38,    8  }, { 0x39,    8  }, { 0x3A,    8  }, { 0x3B,    8  }, 
            { 0x3C,    4  }, { 0x3D,    4  }, { 0x3E,    8  }, { 0x3F,    4  }, { 0x40,    4  }, 
            { 0x41,    4  }, { 0x42,    4  }, { 0x43,    4  }, { 0x44,    4  }, { 0x45,    4  }, 
            { 0x46,    8  }, { 0x47,    4  }, { 0x48,    4  }, { 0x49,    4  }, { 0x4A,    4  }, 
            { 0x4B,    4  }, { 0x4C,    4  }, { 0x4D,    4  }, { 0x4E,    8  }, { 0x4F,    4  }, 
            { 0x50,    4  }, { 0x51,    4  }, { 0x52,    4  }, { 0x53,    4  }, { 0x54,    4  }, 
            { 0x55,    4  }, { 0x56,    8  }, { 0x57,    4  }, { 0x58,    4  }, { 0x59,    4  }, 
            { 0x5A,    4  }, { 0x5B,    4  }, { 0x5C,    4  }, { 0x5D,    4  }, { 0x5E,    8  }, 
            { 0x5F,    4  }, { 0x60,    4  }, { 0x61,    4  }, { 0x62,    4  }, { 0x63,    4  }, 
            { 0x64,    4  }, { 0x65,    4  }, { 0x66,    8  }, { 0x67,    4  }, { 0x68,    4  }, 
            { 0x69,    4  }, { 0x6A,    4  }, { 0x6B,    4  }, { 0x6C,    4  }, { 0x6D,    4  }, 
            { 0x6E,    8  }, { 0x6F,    4  }, { 0x70,    8  }, { 0x71,    8  }, { 0x72,    8  }, 
            { 0x73,    8  }, { 0x74,    8  }, { 0x75,    8  }, { 0x76,    4  }, { 0x77,    8  }, 
            { 0x78,    4  }, { 0x79,    4  }, { 0x7A,    4  }, { 0x7B,    4  }, { 0x7C,    4  }, 
            { 0x7D,    4  }, { 0x7E,    8  }, { 0x7F,    4  }, { 0x80,    4  }, { 0x81,    4  }, 
            { 0x82,    4  }, { 0x83,    4  }, { 0x84,    4  }, { 0x85,    4  }, { 0x86,    8  }, 
            { 0x87,    4  }, { 0x88,    4  }, { 0x89,    4  }, { 0x8A,    4  }, { 0x8B,    4  }, 
            { 0x8C,    4  }, { 0x8D,    4  }, { 0x8E,    8  }, { 0x8F,    4  }, { 0x90,    4  }, 
            { 0x91,    4  }, { 0x92,    4  }, { 0x93,    4  }, { 0x94,    4  }, { 0x95,    4  }, 
            { 0x96,    8  }, { 0x97,    4  }, { 0x98,    4  }, { 0x99,    4  }, { 0x9A,    4  }, 
            { 0x9B,    4  }, { 0x9C,    4  }, { 0x9D,    4  }, { 0x9E,    8  }, { 0x9F,    4  }, 
            { 0xA0,    4  }, { 0xA1,    4  }, { 0xA2,    4  }, { 0xA3,    4  }, { 0xA4,    4  }, 
            { 0xA5,    4  }, { 0xA6,    8  }, { 0xA7,    4  }, { 0xA8,    4  }, { 0xA9,    4  }, 
            { 0xAA,    4  }, { 0xAB,    4  }, { 0xAC,    4  }, { 0xAD,    4  }, { 0xAE,    8  }, 
            { 0xAF,    4  }, { 0xB0,    4  }, { 0xB1,    4  }, { 0xB2,    4  }, { 0xB3,    4  }, 
            { 0xB4,    4  }, { 0xB5,    4  }, { 0xB6,    8  }, { 0xB7,    4  }, { 0xB8,    4  }, 
            { 0xB9,    4  }, { 0xBA,    4  }, { 0xBB,    4  }, { 0xBC,    4  }, { 0xBD,    4  }, 
            { 0xBE,    8  }, { 0xBF,    4  }, { 0xC0,    8  }, { 0xC1,    12 }, { 0xC2,    12 }, 
            { 0xC3,    12 }, { 0xC4,    12 }, { 0xC5,    16 }, { 0xC6,    8  }, { 0xC7,    32 }, 
            { 0xC8,    8  }, { 0xC9,    8  }, { 0xCA,    12 }, { 0xCB00,  8  }, { 0xCB01,  8  }, 
            { 0xCB02,  8  }, { 0xCB03,  8  }, { 0xCB04,  8  }, { 0xCB05,  8  }, { 0xCB06,  16 }, 
            { 0xCB07,  8  }, { 0xCB08,  8  }, { 0xCB09,  8  }, { 0xCB0A,  8  }, { 0xCB0B,  8  }, 
            { 0xCB0C,  8  }, { 0xCB0D,  8  }, { 0xCB0E,  16 }, { 0xCB0F,  8  }, { 0xCB10,  8  }, 
            { 0xCB11,  8  }, { 0xCB12,  8  }, { 0xCB13,  8  }, { 0xCB14,  8  }, { 0xCB15,  8  }, 
            { 0xCB16,  16 }, { 0xCB17,  8  }, { 0xCB18,  8  }, { 0xCB19,  8  }, { 0xCB1A,  8  }, 
            { 0xCB1B,  8  }, { 0xCB1C,  8  }, { 0xCB1D,  8  }, { 0xCB1E,  16 }, { 0xCB1F,  8  }, 
            { 0xCB20,  8  }, { 0xCB21,  8  }, { 0xCB22,  8  }, { 0xCB23,  8  }, { 0xCB24,  8  }, 
            { 0xCB25,  8  }, { 0xCB26,  16 }, { 0xCB27,  8  }, { 0xCB28,  8  }, { 0xCB29,  8  }, 
            { 0xCB2A,  8  }, { 0xCB2B,  8  }, { 0xCB2C,  8  }, { 0xCB2D,  8  }, { 0xCB2E,  16 }, 
            { 0xCB2F,  8  }, { 0xCB30,  8  }, { 0xCB31,  8  }, { 0xCB32,  8  }, { 0xCB33,  8  }, 
            { 0xCB34,  8  }, { 0xCB35,  8  }, { 0xCB36,  16 }, { 0xCB37,  8  }, { 0xCB38,  8  }, 
            { 0xCB39,  8  }, { 0xCB3A,  8  }, { 0xCB3B,  8  }, { 0xCB3C,  8  }, { 0xCB3D,  8  }, 
            { 0xCB3E,  16 }, { 0xCB3F,  8  }, { 0xCB40,  8  }, { 0xCB41,  8  }, { 0xCB42,  8  }, 
            { 0xCB43,  8  }, { 0xCB44,  8  }, { 0xCB45,  8  }, { 0xCB46,  16 }, { 0xCB47,  8  }, 
            { 0xCB48,  8  }, { 0xCB49,  8  }, { 0xCB4A,  8  }, { 0xCB4B,  8  }, { 0xCB4C,  8  }, 
            { 0xCB4D,  8  }, { 0xCB4E,  16 }, { 0xCB4F,  8  }, { 0xCB50,  8  }, { 0xCB51,  8  }, 
            { 0xCB52,  8  }, { 0xCB53,  8  }, { 0xCB54,  8  }, { 0xCB55,  8  }, { 0xCB56,  16 }, 
            { 0xCB57,  8  }, { 0xCB58,  8  }, { 0xCB59,  8  }, { 0xCB5A,  8  }, { 0xCB5B,  8  }, 
            { 0xCB5C,  8  }, { 0xCB5D,  8  }, { 0xCB5E,  16 }, { 0xCB5F,  8  }, { 0xCB60,  8  }, 
            { 0xCB61,  8  }, { 0xCB62,  8  }, { 0xCB63,  8  }, { 0xCB64,  8  }, { 0xCB65,  8  }, 
            { 0xCB66,  16 }, { 0xCB67,  8  }, { 0xCB68,  8  }, { 0xCB69,  8  }, { 0xCB6A,  8  }, 
            { 0xCB6B,  8  }, { 0xCB6C,  8  }, { 0xCB6D,  8  }, { 0xCB6E,  16 }, { 0xCB6F,  8  }, 
            { 0xCB70,  8  }, { 0xCB71,  8  }, { 0xCB72,  8  }, { 0xCB73,  8  }, { 0xCB74,  8  }, 
            { 0xCB75,  8  }, { 0xCB76,  16 }, { 0xCB77,  8  }, { 0xCB78,  8  }, { 0xCB79,  8  }, 
            { 0xCB7A,  8  }, { 0xCB7B,  8  }, { 0xCB7C,  8  }, { 0xCB7D,  8  }, { 0xCB7E,  16 }, 
            { 0xCB7F,  8  }, { 0xCB80,  8  }, { 0xCB81,  8  }, { 0xCB82,  8  }, { 0xCB83,  8  }, 
            { 0xCB84,  8  }, { 0xCB85,  8  }, { 0xCB86,  16 }, { 0xCB87,  8  }, { 0xCB88,  8  }, 
            { 0xCB89,  8  }, { 0xCB8A,  8  }, { 0xCB8B,  8  }, { 0xCB8C,  8  }, { 0xCB8D,  8  }, 
            { 0xCB8E,  16 }, { 0xCB8F,  8  }, { 0xCB90,  8  }, { 0xCB91,  8  }, { 0xCB92,  8  }, 
            { 0xCB93,  8  }, { 0xCB94,  8  }, { 0xCB95,  8  }, { 0xCB96,  16 }, { 0xCB97,  8  }, 
            { 0xCB98,  8  }, { 0xCB99,  8  }, { 0xCB9A,  8  }, { 0xCB9B,  8  }, { 0xCB9C,  8  }, 
            { 0xCB9D,  8  }, { 0xCB9E,  16 }, { 0xCB9F,  8  }, { 0xCBA0,  8  }, { 0xCBA1,  8  }, 
            { 0xCBA2,  8  }, { 0xCBA3,  8  }, { 0xCBA4,  8  }, { 0xCBA5,  8  }, { 0xCBA6,  16 }, 
            { 0xCBA7,  8  }, { 0xCBA8,  8  }, { 0xCBA9,  8  }, { 0xCBAA,  8  }, { 0xCBAB,  8  }, 
            { 0xCBAC,  8  }, { 0xCBAD,  8  }, { 0xCBAE,  16 }, { 0xCBAF,  8  }, { 0xCBB0,  8  }, 
            { 0xCBB1,  8  }, { 0xCBB2,  8  }, { 0xCBB3,  8  }, { 0xCBB4,  8  }, { 0xCBB5,  8  }, 
            { 0xCBB6,  16 }, { 0xCBB7,  8  }, { 0xCBB8,  8  }, { 0xCBB9,  8  }, { 0xCBBA,  8  }, 
            { 0xCBBB,  8  }, { 0xCBBC,  8  }, { 0xCBBD,  8  }, { 0xCBBE,  16 }, { 0xCBBF,  8  }, 
            { 0xCBC0,  8  }, { 0xCBC1,  8  }, { 0xCBC2,  8  }, { 0xCBC3,  8  }, { 0xCBC4,  8  }, 
            { 0xCBC5,  8  }, { 0xCBC6,  16 }, { 0xCBC7,  8  }, { 0xCBC8,  8  }, { 0xCBC9,  8  }, 
            { 0xCBCA,  8  }, { 0xCBCB,  8  }, { 0xCBCC,  8  }, { 0xCBCD,  8  }, { 0xCBCE,  16 }, 
            { 0xCBCF,  8  }, { 0xCBD0,  8  }, { 0xCBD1,  8  }, { 0xCBD2,  8  }, { 0xCBD3,  8  }, 
            { 0xCBD4,  8  }, { 0xCBD5,  8  }, { 0xCBD6,  16 }, { 0xCBD7,  8  }, { 0xCBD8,  8  }, 
            { 0xCBD9,  8  }, { 0xCBDA,  8  }, { 0xCBDB,  8  }, { 0xCBDC,  8  }, { 0xCBDD,  8  }, 
            { 0xCBDE,  16 }, { 0xCBDF,  8  }, { 0xCBE0,  8  }, { 0xCBE1,  8  }, { 0xCBE2,  8  }, 
            { 0xCBE3,  8  }, { 0xCBE4,  8  }, { 0xCBE5,  8  }, { 0xCBE6,  16 }, { 0xCBE7,  8  }, 
            { 0xCBE8,  8  }, { 0xCBE9,  8  }, { 0xCBEA,  8  }, { 0xCBEB,  8  }, { 0xCBEC,  8  }, 
            { 0xCBED,  8  }, { 0xCBEE,  16 }, { 0xCBEF,  8  }, { 0xCBF0,  8  }, { 0xCBF1,  8  }, 
            { 0xCBF2,  8  }, { 0xCBF3,  8  }, { 0xCBF4,  8  }, { 0xCBF5,  8  }, { 0xCBF6,  16 }, 
            { 0xCBF7,  8  }, { 0xCBF8,  8  }, { 0xCBF9,  8  }, { 0xCBFA,  8  }, { 0xCBFB,  8  }, 
            { 0xCBFC,  8  }, { 0xCBFD,  8  }, { 0xCBFE,  16 }, { 0xCBFF,  8  }, { 0xCC,    12 }, 
            { 0xCD,    12 }, { 0xCE,    8  }, { 0xCF,    32 }, { 0xD0,    8  }, { 0xD1,    12 }, 
            { 0xD2,    12 }, { 0xD4,    12 }, { 0xD5,    16 }, { 0xD6,    8  }, { 0xD7,    32 }, 
            { 0xD8,    8  }, { 0xD9,    8  }, { 0xDA,    12 }, { 0xDC,    12 }, { 0xDE,    8  }, 
            { 0xDF,    32 }, { 0xE0,    12 }, { 0xE1,    12 }, { 0xE2,    8  }, { 0xE5,    16 }, 
            { 0xE6,    8  }, { 0xE7,    32 }, { 0xE8,    16 }, { 0xE9,    4  }, { 0xEA,    16 }, 
            { 0xEE,    8  }, { 0xEF,    32 }, { 0xF0,    12 }, { 0xF1,    12 }, { 0xF2,    8  }, 
            { 0xF3,    4  }, { 0xF5,    16 }, { 0xF6,    8  }, { 0xF7,    32 }, { 0xF8,    12 }, 
            { 0xF9,    8  }, { 0xFA,    16 }, { 0xFB,    4  }, { 0xFE,    8  }, { 0xFF,    32 }
        };
    }
}
