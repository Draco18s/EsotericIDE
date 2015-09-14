﻿using System;
using System.Collections.Generic;
using System.Linq;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace EsotericIDE.Ziim
{
    sealed class ZiimEnv : ExecutionEnvironment
    {
        public List<Node> Nodes;
        public List<Thread> Threads;
        public string Input;

        public override string DescribeExecutionState()
        {
            var firstUnsuspended = Threads.IndexOf(t => !t.Suspended);
            return Threads.Select((t, i) =>
                "{0} {1,3}: {2} {3}".Fmt(
                    t.Suspended ? "·" : i == firstUnsuspended ? "→" : " ",
                    i,
                    t.CurrentInstruction.Instruction,
                    t.CurrentValue
                )
            ).JoinString(Environment.NewLine);
        }

        protected override void Run()
        {
            RuntimeError error = null;

            while (State == ExecutionState.Debugging || State == ExecutionState.Running)
            {
                var activeThread = Threads.FirstOrDefault(t => !t.Suspended);
                if (activeThread == null)
                    // No threads left or all threads suspended
                    break;

                if (activeThread.CurrentInstruction == null)
                {
                    _output.Append(activeThread.CurrentValue.ToOutput());
                    goto finished;
                }

                lock (_locker)
                    if (_breakpoints != null && _breakpoints.Contains(activeThread.CurrentInstruction.Index))
                        State = ExecutionState.Debugging;

                if (State == ExecutionState.Debugging)
                {
                    fireDebuggerBreak(new Position(activeThread.CurrentInstruction.Index, 1));
                    _resetEvent.Reset();
                    _resetEvent.WaitOne();
                }

                switch (activeThread.CurrentInstruction.Instruction)
                {
                    case Instruction.Zero:
                        {
                            activeThread.CurrentValue = Bits.Zero;
                            activeThread.CurrentInstruction = activeThread.CurrentInstruction.PointsTo[0];
                            break;
                        }

                    case Instruction.Stdin:
                        {
                            if (string.IsNullOrEmpty(Input))
                                activeThread.CurrentValue = Bits.Empty;
                            else if (char.IsSurrogate(Input, 0))
                            {
                                activeThread.CurrentValue = Bits.FromString(Input.Substring(0, 2));
                                Input = Input.Substring(2);
                            }
                            else
                            {
                                activeThread.CurrentValue = Bits.FromString(Input.Substring(0, 1));
                                Input = Input.Substring(1);
                            }
                            activeThread.CurrentInstruction = activeThread.CurrentInstruction.PointsTo[0];
                            break;
                        }

                    case Instruction.Concat:
                        {
                            activeThread.Role = activeThread.CurrentInstruction.PointedToBy.IndexOf(activeThread.PrevInstruction);
                            Ut.Assert(activeThread.Role == 0 || activeThread.Role == 1);
                            var otherThread = Threads.FirstOrDefault(t => t.Suspended && t.CurrentInstruction == activeThread.CurrentInstruction && t.Role == (activeThread.Role ^ 1));
                            if (otherThread != null)
                            {
                                activeThread.CurrentValue = activeThread.Role == 0 ? activeThread.CurrentValue.Concat(otherThread.CurrentValue) : otherThread.CurrentValue.Concat(activeThread.CurrentValue);
                                activeThread.CurrentInstruction = activeThread.CurrentInstruction.PointsTo[0];
                                Threads.Remove(otherThread);
                            }
                            else
                                activeThread.Suspended = true;
                            break;
                        }

                    case Instruction.NoOp:
                    case Instruction.Label:
                        {
                            activeThread.CurrentInstruction = activeThread.CurrentInstruction.PointsTo[0];
                            break;
                        }

                    case Instruction.Inverse:
                        {
                            activeThread.CurrentValue = activeThread.CurrentValue.Invert();
                            activeThread.CurrentInstruction = activeThread.CurrentInstruction.PointsTo[0];
                            break;
                        }

                    case Instruction.Splitter:
                        {
                            var newThread = new Thread();
                            newThread.CurrentInstruction = activeThread.CurrentInstruction; // necessary for PrevInstruction to be correct
                            newThread.CurrentInstruction = activeThread.CurrentInstruction.PointsTo[1];
                            newThread.CurrentValue = activeThread.CurrentValue;
                            Threads.Add(newThread);
                            activeThread.CurrentInstruction = activeThread.CurrentInstruction.PointsTo[0];
                            break;
                        }

                    case Instruction.IsZero:
                        {
                            if (activeThread.CurrentValue.IsEmpty)
                                Threads.Remove(activeThread);
                            var iz = activeThread.CurrentValue.IsFirstBitZero;
                            activeThread.CurrentValue = activeThread.CurrentValue.RemoveFirstBit();
                            activeThread.CurrentInstruction = activeThread.CurrentInstruction.PointsTo[iz ? 1 : 0];
                        }
                        break;

                    case Instruction.IsEmpty:
                        {
                            activeThread.CurrentInstruction = activeThread.CurrentInstruction.PointsTo[activeThread.CurrentValue.IsEmpty ? 0 : 1];
                            activeThread.CurrentValue = Bits.Empty;
                        }
                        break;

                    default:
                        error = new RuntimeError(new Position(activeThread.CurrentInstruction.Index, 1), "Unrecognized instruction encountered. (This indicates a bug in the parser, which should have caught this at compile-time.)");
                        goto finished;
                }
            }

            finished:
            fireExecutionFinished(State == ExecutionState.Stop || error != null, error);
            State = ExecutionState.Finished;
            _resetEvent.Reset();
        }
    }
}
