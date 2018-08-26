﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Codecs
{
    using System;
    using System.Collections.Generic;
    using DotNetty.Buffers;
    using DotNetty.Transport.Channels;

    public abstract class ReplayingDecoder<TState> : ByteToMessageDecoder
        where TState : struct
    {
        TState state;
        int checkpoint;
        bool replayRequested;

        protected ReplayingDecoder(TState initialState)
        {
            this.state = initialState;
        }

        protected TState State => this.state;

        protected void Checkpoint()
        {
            this.checkpoint = this.InternalBuffer.ReaderIndex;
        }

        protected void Checkpoint(TState newState)
        {
            this.Checkpoint();
            this.state = newState;
        }

        protected bool ReplayRequested => this.replayRequested;

        protected void RequestReplay()
        {
            this.replayRequested = true;
        }

        protected override void CallDecode(IChannelHandlerContext context, IByteBuffer input, List<object> output)
        {
            try
            {
                while (input.IsReadable())
                {
                    this.replayRequested = false;
                    int oldReaderIndex = this.checkpoint = input.ReaderIndex;
                    int outSize = output.Count;

                    if (outSize > 0)
                    {
                        for (int i = 0; i < outSize; i++)
                        {
                            context.FireChannelRead(output[i]);
                        }
                        output.Clear();

                        // Check if this handler was removed before continuing with decoding.
                        // If it was removed, it is not safe to continue to operate on the buffer.
                        //
                        // See:
                        // - https://github.com/netty/netty/issues/4635
                        if (context.Removed)
                        {
                            break;
                        }
                        outSize = 0;
                    }

                    TState oldState = this.state;
                    int oldInputLength = input.ReadableBytes;
                    this.DecodeRemovalReentryProtection(context, input, output);

                    if (this.replayRequested)
                    {
                        // Check if this handler was removed before continuing the loop.
                        // If it was removed, it is not safe to continue to operate on the buffer.
                        //
                        // See https://github.com/netty/netty/issues/1664
                        if (context.Removed)
                        {
                            break;
                        }

                        // Return to the checkpoint (or oldPosition) and retry.
                        int restorationPoint = this.checkpoint;
                        if (restorationPoint >= 0)
                        {
                            input.SetReaderIndex(restorationPoint);
                        }
                        else
                        {
                            // Called by cleanup() - no need to maintain the readerIndex
                            // anymore because the buffer has been released already.
                        }
                        break;
                    }

                    // Check if this handler was removed before continuing the loop.
                    // If it was removed, it is not safe to continue to operate on the buffer.
                    //
                    // See https://github.com/netty/netty/issues/1664
                    if (context.Removed)
                    {
                        break;
                    }

                    if (outSize == output.Count)
                    {
                        if (oldInputLength == input.ReadableBytes && s_comparer.Equals(oldState, this.state))
                        {
                            ThrowHelper.ThrowDecoderException_Anything(this.GetType());
                        }
                        else
                        {
                            // Previous data has been discarded or caused state transition.
                            // Probably it is reading on.
                            continue;
                        }
                    }

                    if (oldReaderIndex == input.ReaderIndex && s_comparer.Equals(oldState, this.state))
                    {
                        ThrowHelper.ThrowDecoderException_Something(this.GetType());
                    }

                    if (this.SingleDecode)
                    {
                        break;
                    }
                }
            }
            catch (DecoderException)
            {
                throw;
            }
            catch (Exception cause)
            {
                ThrowHelper.ThrowDecoderException(cause);
            }
        }

        //// ReSharper disable once RedundantOverridenMember decoder needs to be "aware" when read passes through so that it can detect situation where it did not fire reads for the next handler and autoRead is false
        //public override void Read(IChannelHandlerContext context)
        //{
        //    base.Read(context);
        //}

        private static readonly IEqualityComparer<TState> s_comparer = EqualityComparer<TState>.Default;
    }
}