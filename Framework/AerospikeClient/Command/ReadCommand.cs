/* 
 * Copyright 2012-2020 Aerospike, Inc.
 *
 * Portions may be licensed to Aerospike, Inc. under one or more contributor
 * license agreements.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy of
 * the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations under
 * the License.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Aerospike.Client
{
	public class ReadCommand : SyncCommand
	{
		protected readonly Key key;
		private readonly string[] binNames;
		protected readonly Partition partition;
		private Record record;

		public ReadCommand(Cluster cluster, Policy policy, Key key)
			: base(cluster, policy, LatencyType.READ)
		{
			this.key = key;
			this.binNames = null;
			this.partition = Partition.Read(cluster, policy, key);
		}

		public ReadCommand(Cluster cluster, Policy policy, Key key, String[] binNames)
			: base(cluster, policy, LatencyType.READ)
		{
			this.key = key;
			this.binNames = binNames;
			this.partition = Partition.Read(cluster, policy, key);
		}

		public ReadCommand(Cluster cluster, Policy policy, Key key, Partition partition)
			: base(cluster, policy, LatencyType.READ)
		{
			this.key = key;
			this.binNames = null;
			this.partition = partition;
		}

		protected internal override Node GetNode()
		{
			return partition.GetNodeRead(cluster);
		}

		protected internal override void WriteBuffer()
		{
			SetRead(policy, key, binNames);
		}

		protected internal override void ParseResult(Connection conn)
		{
			// Read header.		
			conn.ReadFully(dataBuffer, 8);

			long sz = ByteUtil.BytesToLong(dataBuffer, 0);
			int receiveSize = (int)(sz & 0xFFFFFFFFFFFFL);

			if (receiveSize <= 0)
			{
				throw new AerospikeException("Invalid receive size: " + receiveSize);
			}

			SizeBuffer(receiveSize);
			conn.ReadFully(dataBuffer, receiveSize);
			conn.UpdateLastUsed();

			ulong type = (ulong)((sz >> 48) & 0xff);

			if (type == Command.AS_MSG_TYPE)
			{
				dataOffset = 5;
			}
			else if (type == Command.MSG_TYPE_COMPRESSED)
			{
				int usize = (int)ByteUtil.BytesToLong(dataBuffer, 0);
				byte[] ubuf = new byte[usize];

				ByteUtil.Decompress(dataBuffer, 8, receiveSize, ubuf, usize);
				dataBuffer = ubuf;
				dataOffset = 13;
			}
			else
			{
				throw new AerospikeException("Invalid proto type: " + type + " Expected: " + Command.AS_MSG_TYPE);
			}
					
			int resultCode = dataBuffer[dataOffset];
			dataOffset++;
			int generation = ByteUtil.BytesToInt(dataBuffer, dataOffset);
			dataOffset += 4;
			int expiration = ByteUtil.BytesToInt(dataBuffer, dataOffset);
			dataOffset += 8;
			int fieldCount = ByteUtil.BytesToShort(dataBuffer, dataOffset);
			dataOffset += 2;
			int opCount = ByteUtil.BytesToShort(dataBuffer, dataOffset);
			dataOffset += 2;

			if (resultCode == 0)
			{
				if (opCount == 0)
				{
					// Bin data was not returned.
					record = new Record(null, generation, expiration);
					return;
				}
				record = ParseRecord(opCount, fieldCount, generation, expiration);
				return;
			}

			if (resultCode == ResultCode.KEY_NOT_FOUND_ERROR)
			{
				HandleNotFound(resultCode);
				return;
			}

			if (resultCode == ResultCode.FILTERED_OUT)
			{
				if (policy.failOnFilteredOut)
				{
					throw new AerospikeException(resultCode);
				}
				return;
			}

			if (resultCode == ResultCode.UDF_BAD_RESPONSE)
			{
				record = ParseRecord(opCount, fieldCount, generation, expiration);
				HandleUdfError(resultCode);
				return;
			}

			throw new AerospikeException(resultCode);
		}

		protected internal override bool PrepareRetry(bool timeout)
		{
			partition.PrepareRetryRead(timeout);
			return true;
		}

		protected internal virtual void HandleNotFound(int resultCode)
		{
			// Do nothing in default case. Record will be null.
		}

		private void HandleUdfError(int resultCode)
		{
			object obj;

			if (!record.bins.TryGetValue("FAILURE", out obj))
			{
				throw new AerospikeException(resultCode);
			}

			string ret = (string)obj;
			string message;
			int code;

			try
			{
				string[] list = ret.Split(':');
				code = Convert.ToInt32(list[2].Trim());
				message = list[0] + ':' + list[1] + ' ' + list[3];
			}
			catch (Exception)
			{
				// Use generic exception if parse error occurs.
				throw new AerospikeException(resultCode, ret);
			}

			throw new AerospikeException(code, message);
		}

		private Record ParseRecord(int opCount, int fieldCount, int generation, int expiration)
		{
			Dictionary<string, object> bins = null;

			// There can be fields in the response (setname etc).
			// But for now, ignore them. Expose them to the API if needed in the future.
			if (fieldCount != 0)
			{
				// Just skip over all the fields
				for (int i = 0; i < fieldCount; i++)
				{
					int fieldSize = ByteUtil.BytesToInt(dataBuffer, dataOffset);
					dataOffset += 4 + fieldSize;
				}
			}

			for (int i = 0 ; i < opCount; i++)
			{
				int opSize = ByteUtil.BytesToInt(dataBuffer, dataOffset);
				dataOffset += 5;
				byte particleType = dataBuffer[dataOffset];
				dataOffset += 2;
				byte nameSize = dataBuffer[dataOffset++];
				string name = ByteUtil.Utf8ToString(dataBuffer, dataOffset, nameSize);
				dataOffset += nameSize;

				int particleBytesSize = (int)(opSize - (4 + nameSize));
				object value = ByteUtil.BytesToParticle(particleType, dataBuffer, dataOffset, particleBytesSize);
				dataOffset += particleBytesSize;

				if (bins == null)
				{
					bins = new Dictionary<string, object>();
				}
				AddBin(bins, name, value);
			}
			return new Record(bins, generation, expiration);
		}

		protected internal virtual void AddBin(Dictionary<string, object> bins, string name, object value)
		{
			bins[name] = value;
		}

		public Record Record
		{
			get
			{
				return record;
			}
		}
	}
}
