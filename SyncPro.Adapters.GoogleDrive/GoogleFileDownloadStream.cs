﻿namespace SyncPro.Adapters.GoogleDrive
{
    using System;
    using System.IO;
    using System.Net.Http;

    public class GoogleFileDownloadStream : Stream
    {
        private readonly GoogleDriveClient client;
        private readonly string itemId;

        private const int fragmentLength = 10485760;

        // Initialize to an empty array will trigger a download on the first read.
        private byte[] currentBuffer = new byte[0];

        // The offset in the current buffer where the next byte should be read from
        private int currentBufferOffset;

        // The next fragment to request
        private int currentFragmentOffset;

        private bool isFinalBuffer;

        internal GoogleFileDownloadStream(GoogleDriveClient client, string itemId)
        {
            this.client = client;
            this.itemId = itemId;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                // Check if we need to download a fragment
                if (this.currentBufferOffset >= this.currentBuffer.Length)
                {
                    if (this.isFinalBuffer)
                    {
                        return i;
                    }

                    HttpResponseMessage response = this.client.DownloadFileFragment(
                        this.itemId,
                        this.currentFragmentOffset,
                        fragmentLength).Result;

                    this.currentBuffer = response.Content.ReadAsByteArrayAsync().Result;

                    this.currentFragmentOffset++;
                    this.currentBufferOffset = 0;

                    var rangeHeader = response.Content.Headers.ContentRange;
                    Pre.Assert(rangeHeader.To != null, "rangeHeader.To != null");
                    Pre.Assert(rangeHeader.Length != null, "rangeHeader.Length != null");

                    if (rangeHeader.To.Value == rangeHeader.Length - 1)
                    {
                        this.isFinalBuffer = true;
                    }
                }

                buffer[i + offset] = this.currentBuffer[this.currentBufferOffset];

                this.currentBufferOffset++;
            }

            return count;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }
    }
}