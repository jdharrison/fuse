﻿using UnityEditor;

namespace iMVC
{
	public static class Utils
	{
		public static void PreparePath(string path)
		{
			string[] directories = path.Split('/');
			string parent = string.Empty;
			for (int i = 0; i <= directories.Length - 1; i++)
			{
				string current = directories[i];
				string next = parent == string.Empty ? current : parent + "/" + current;
				if (!AssetDatabase.IsValidFolder(next))
					AssetDatabase.CreateFolder(parent, current);
				parent = next;
			}
		}
	}
}