using System;
using System.Globalization;
using System.IO;

public static class SteamAppId
{
	private const string FileName = "steam_appid.txt";

	public static bool TryReadDevelopmentFile(out uint appId)
	{
		foreach (var path in GetCandidatePaths())
		{
			if (!File.Exists(path))
			{
				continue;
			}

			var text = File.ReadAllText(path).Trim();
			if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out appId))
			{
				return true;
			}
		}

		appId = 0;
		return false;
	}

	public static void WriteDevelopmentFileIfMissing(uint appId)
	{
		try
		{
			var path = Path.Combine(Directory.GetCurrentDirectory(), FileName);
			if (File.Exists(path))
			{
				return;
			}

			File.WriteAllText(path, appId.ToString(CultureInfo.InvariantCulture));
		}
		catch
		{
		}
	}

	private static string[] GetCandidatePaths()
	{
		return
		[
			Path.Combine(Directory.GetCurrentDirectory(), FileName),
			Path.Combine(AppContext.BaseDirectory, FileName),
		];
	}
}
