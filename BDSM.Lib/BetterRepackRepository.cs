using System.Collections.Immutable;

using FluentFTP;

using static BDSM.Lib.Configuration;

namespace BDSM.Lib;

public static class BetterRepackRepositoryDefinitions
{
	public const  string MainModpackName             = "Sideloader Modpack";
	public const  string MeShadersModpackName        = "Sideloader Modpack - MaterialEditor Shaders";
	public const  string UncensorSelectorModpackName = "Sideloader Modpack - Uncensor Selector";
	public const  string ExclusiveHs2ModpackName     = "Sideloader Modpack - Exclusive HS2";
	public const  string ExclusiveAisModpackName     = "Sideloader Modpack - Exclusive AIS";
	public const  string StudioMapsModpackName       = "Sideloader Modpack - Maps";
	public const  string Hs2MapsModpackName          = "Sideloader Modpack - Maps (HS2 Game)";
	public const  string BleedingEdgeModpackName     = "Sideloader Modpack - Bleeding Edge";
	public const  string StudioModpackName           = "Sideloader Modpack - Studio";
	public const  string UserDataDirectoryName       = "UserData";
	private const string UserDataHs2ModpackName      = "UserData (HS2)";
	private const string UserDataAisModpackName      = "UserData (AIS)";

	public static readonly ImmutableDictionary<string, ModpackDefinition> AllBasePathMappings =
		new Dictionary<string, ModpackDefinition>
		{
			{
				MainModpackName,
				new ModpackDefinition
				{
					Name               = MainModpackName,
					RemoteRelativePath = "mods/Sideloader Modpack",
					LocalRelativePath  = "mods/Sideloader Modpack",
					DeleteClientFiles  = true
				}
			},
			{
				ExclusiveHs2ModpackName,
				new ModpackDefinition
				{
					Name               = ExclusiveHs2ModpackName,
					RemoteRelativePath = "mods/Sideloader Modpack - Exclusive HS2",
					LocalRelativePath  = "mods/Sideloader Modpack - Exclusive HS2",
					DeleteClientFiles  = true
				}
			},
			{
				ExclusiveAisModpackName,
				new ModpackDefinition
				{
					Name               = ExclusiveAisModpackName,
					RemoteRelativePath = "mods/Sideloader Modpack - Exclusive AIS",
					LocalRelativePath  = "mods/Sideloader Modpack - Exclusive AIS",
					DeleteClientFiles  = true
				}
			},
			{
				StudioMapsModpackName,
				new ModpackDefinition
				{
					Name               = StudioMapsModpackName,
					RemoteRelativePath = "mods/Sideloader Modpack - Maps",
					LocalRelativePath  = "mods/Sideloader Modpack - Maps",
					DeleteClientFiles  = true
				}
			},
			{
				Hs2MapsModpackName,
				new ModpackDefinition
				{
					Name               = Hs2MapsModpackName,
					RemoteRelativePath = "mods/Sideloader Modpack - Maps (HS2 Game)",
					LocalRelativePath  = "mods/Sideloader Modpack - Maps (HS2 Game)",
					DeleteClientFiles  = true
				}
			},
			{
				MeShadersModpackName,
				new ModpackDefinition
				{
					Name               = MeShadersModpackName,
					RemoteRelativePath = "mods/Sideloader Modpack - MaterialEditor Shaders",
					LocalRelativePath  = "mods/Sideloader Modpack - MaterialEditor Shaders",
					DeleteClientFiles  = true
				}
			},
			{
				StudioModpackName,
				new ModpackDefinition
				{
					Name               = StudioModpackName,
					RemoteRelativePath = "mods/Sideloader Modpack - Studio",
					LocalRelativePath  = "mods/Sideloader Modpack - Studio",
					DeleteClientFiles  = true
				}
			},
			{
				BleedingEdgeModpackName,
				new ModpackDefinition
				{
					Name               = BleedingEdgeModpackName,
					RemoteRelativePath = "mods/SideloaderModpack-BleedingEdge",
					LocalRelativePath  = "mods/Sideloader Modpack - Bleeding Edge",
					DeleteClientFiles  = true
				}
			},
			{
				UncensorSelectorModpackName,
				new ModpackDefinition
				{
					Name               = UncensorSelectorModpackName,
					RemoteRelativePath = "mods/SideloaderModpack-UncensorSelector",
					LocalRelativePath  = "mods/Sideloader Modpack - Uncensor Selector",
					DeleteClientFiles  = true
				}
			},
			{
				UserDataHs2ModpackName,
				new ModpackDefinition
				{
					Name               = UserDataHs2ModpackName,
					RemoteRelativePath = "UserData-HS2",
					LocalRelativePath  = "UserData",
					DeleteClientFiles  = false
				}
			},
			{
				UserDataAisModpackName,
				new ModpackDefinition
				{
					Name               = UserDataAisModpackName,
					RemoteRelativePath = "UserData-AIS",
					LocalRelativePath  = "UserData",
					DeleteClientFiles  = false
				}
			}
		}.ToImmutableDictionary();

	private static readonly ImmutableHashSet<string> CommonModpacks = new HashSet<string>
	{
		"Sideloader Modpack",
		"Sideloader Modpack - MaterialEditor Shaders",
		"Sideloader Modpack - Uncensor Selector"
	}.ToImmutableHashSet();

	public static readonly RepoConnectionInfo DefaultConnectionInfo = new()
	{
		Address        = "sideload.betterrepack.com",
		Username       = "sideloader",
		Password       = "sideloader3",
		Port           = 2121,
		RootPath       = "/AI/",
		MaxConnections = 5
	};

	public static FtpConfig DefaultRepoConnectionConfig => new()
	{
		EncryptionMode = FtpEncryptionMode.Auto, ValidateAnyCertificate = true, LogToConsole = false
		//ConnectTimeout = 1000,
		//DataConnectionType = FtpDataConnectionType.PASV,
		//SocketKeepAlive = true
	};

	public static IEnumerable<string> DefaultModpackNames(bool isHs2)
	{
		HashSet<string> desiredModpackNames = CommonModpacks.ToHashSet();
		_ = desiredModpackNames.Add(ExclusiveModpack(isHs2));
		if (isHs2)
		{
			_ = desiredModpackNames.Add(Hs2MapsModpackName);
			_ = desiredModpackNames.Add(UserDataHs2ModpackName);
		}
		else
			_ = desiredModpackNames.Add(UserDataAisModpackName);

		return desiredModpackNames;
	}

	private static string UserDataModpackName(bool isHs2) => isHs2 ? UserDataHs2ModpackName : UserDataAisModpackName;
	private static string ExclusiveModpack(bool    isHs2) => isHs2 ? ExclusiveHs2ModpackName : ExclusiveAisModpackName;

	public static ImmutableHashSet<string> GetDesiredModpackNames(bool isHs2,
		SimpleUserConfiguration.Modpacks                               desiredModpacks)
	{
		HashSet<string> desiredModpackNames = CommonModpacks.ToHashSet();
		_ = desiredModpackNames.Add(ExclusiveModpack(isHs2));
		if (isHs2 && desiredModpacks.HS2Maps) _ = desiredModpackNames.Add(Hs2MapsModpackName);
		if (desiredModpacks.Studio) _           = desiredModpackNames.Add(StudioModpackName);
		if (desiredModpacks.StudioMaps) _       = desiredModpackNames.Add(StudioMapsModpackName);
		if (desiredModpacks.BleedingEdge) _     = desiredModpackNames.Add(BleedingEdgeModpackName);
		if (desiredModpacks.Userdata) _         = desiredModpackNames.Add(UserDataModpackName(isHs2));
		return desiredModpackNames.ToImmutableHashSet();
	}

	public static ImmutableHashSet<PathMapping> ModpackNamesToPathMappings(IEnumerable<string> modpackNames,
		string gamepath, string rootpath)
	{
		HashSet<PathMapping> modpackPathmaps = new();
		foreach (string modpackName in modpackNames)
		{
			ModpackDefinition definition = AllBasePathMappings[modpackName];
			_ = modpackPathmaps.Add(ModpackDefinitionToPathMapping(definition, gamepath, rootpath));
		}

		return modpackPathmaps.ToImmutableHashSet();
	}
}