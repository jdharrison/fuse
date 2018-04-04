﻿using System;
using JetBrains.Annotations;
using UnityEngine;

namespace Fuse.Core
{
	public class Environment : ScriptableObject
	{
		public LoadMethod Loading;
		public string HostUri;

		[Tooltip("Negative value turns off version control")]
		public int DefaultVersion = -1;

		public CustomVersion[] CustomVersion;

		public string GetPath(string filePath)
		{
			return string.Format(Constants.AssetsBakedPath, filePath);
		}

		public Uri GetUri(string filePath)
		{
			return new Uri(HostUri + "/" + filePath);
		}

		public int GetVersion(string bundle)
		{
			foreach (CustomVersion custom in CustomVersion)
				if (custom.Bundle == bundle)
					return custom.Version;

			return DefaultVersion;
		}
	}

	[Serializable]
	public class CustomVersion
	{
		[AssetBundleReference]
		public string Bundle;

		[UsedImplicitly, Tooltip("Negative value turns off version control")]
		public int Version = -1;
	}

	public enum LoadMethod
	{
		Baked,
		Online
	}
}