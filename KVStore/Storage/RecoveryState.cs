using BPUtil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KVStore
{
	/// <summary>
	/// Persistent record of database recovery events.  Stored in the data directory, outside the database (which is replaced during recovery).
	/// </summary>
	public class RecoveryState : SerializableObjectJson
	{
		/// <summary>
		/// Number of times the database was found damaged and replaced with a fresh database.
		/// </summary>
		public long recoveryEventCount = 0;
		/// <summary>
		/// Time of the most recent recovery, in seconds since the unix epoch.  0 if there has never been a recovery.
		/// </summary>
		public long lastRecoveryUnixSeconds = 0;
		/// <summary>
		/// Short description of why the most recent recovery happened.
		/// </summary>
		public string lastRecoveryReason = null;

		protected override SerializableObjectJson DeserializeFromJson(string str)
		{
			return Newtonsoft.Json.JsonConvert.DeserializeObject<RecoveryState>(str);
		}

		protected override string SerializeToJson(object obj)
		{
			return Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
		}
	}
}
