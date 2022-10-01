using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace UnityEditor.AddressableAssets.Build.DataBuilders
{
	/// <summary>
	/// Build script used for player builds and running with bundles in the editor, allowing building of multiple catalogs.
	/// </summary>
	[CreateAssetMenu(fileName = "BuildScriptPackedMultiCatalog.asset", menuName = "Addressables/Content Builders/Multi-Catalog Build Script")]
	public class BuildScriptPackedMultiCatalogMode : BuildScriptPackedMode
	{
		#region Multi catalog section

		[SerializeField]
		[Tooltip("What groups should be separated into different catalogs. Leave empty for all groups")]
		private AddressableAssetGroup[] addressableGroups = new AddressableAssetGroup[0];
		
		[SerializeField]
		// [HideInInspector]
		private List<string> builtBundles = new List<string>();
		
		private readonly List<CatalogSetup> m_catalogSetups = new List<CatalogSetup>();

		public override string Name => base.Name + " - Multi-Catalog";

		protected override List<ContentCatalogBuildInfo> GetContentCatalogs(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
		{
			// cleanup
			m_catalogSetups.Clear();
			builtBundles.Clear();
			
			// Prepare catalogs
			ContentCatalogBuildInfo defaultCatalog = null;
			
			if (addressableGroups.Length == 0)
			{
				// Iterating all the groups since no specific groups are selected
				foreach (KeyValuePair<AddressableAssetGroup, List<string>> keyValuePair in aaContext.assetGroupToBundles)
				{
					CatalogSetup catalog = new CatalogSetup(keyValuePair.Key, ResourceManagerRuntimeData.kCatalogAddress);
					m_catalogSetups.Add(catalog);

					if (keyValuePair.Key.IsDefaultGroup())
					{
						defaultCatalog = catalog.BuildInfo;
					}
				}
			}
			else
			{
				//Iterate all the groups that want to be exported as separate catalogs
				foreach (AddressableAssetGroup addressableAssetGroup in addressableGroups)
				{
					CatalogSetup catalog = new CatalogSetup(addressableAssetGroup, ResourceManagerRuntimeData.kCatalogAddress);
					m_catalogSetups.Add(catalog);

					if (addressableAssetGroup.IsDefaultGroup())
					{
						defaultCatalog = catalog.BuildInfo;
					}
				}

				//Be sure the default catalog is initialized this is needed since the default group might not be added to the array
				defaultCatalog ??= new ContentCatalogBuildInfo(ResourceManagerRuntimeData.kCatalogAddress, builderInput.RuntimeCatalogFilename);
			}

			if (defaultCatalog == null)
			{
				Debug.LogError("Default group couldn't be found");
				return default;
			}

			// Assign assets to new catalogs based on included groups
			AddressableAssetProfileSettings profileSettings = aaContext.Settings.profileSettings;
			string profileId = aaContext.Settings.activeProfileId;

			foreach (ContentCatalogDataEntry loc in aaContext.locations)
			{
				CatalogSetup preferredCatalog = GetCorrespondingCatalogSetup(loc, aaContext);
				if (preferredCatalog != null)
				{
					// The location is an asset bundle, update the catalog data
					if (loc.ResourceType == typeof(IAssetBundleResource))
					{
						AddressableAssetSettings settings = preferredCatalog.AddressableAssetGroup.Settings;
						BundledAssetGroupSchema addressableAssetGroupSchema = preferredCatalog.AddressableAssetGroup.GetSchema<BundledAssetGroupSchema>();

						// Update the catalog path to be the respective one for this bundle
						string buildPath = profileSettings.GetValueByName(profileId, addressableAssetGroupSchema.BuildPath.GetName(settings));
						buildPath = profileSettings.EvaluateString(profileId, buildPath);
							
						string filePath = Path.GetFullPath(Path.Combine(buildPath, Path.GetFileName(loc.InternalId)));
						
						if (!File.Exists(filePath))
						{
							filePath = Path.GetFullPath(Path.Combine(Addressables.BuildPath + "/[BuildTarget]", Path.GetFileName(loc.InternalId)));
							filePath = profileSettings.EvaluateString(profileId, filePath);
						}

						string internalId = loc.InternalId.Contains("{UnityEngine.AddressableAssets.Addressables.RuntimePath}")
							? Path.GetFullPath(loc.InternalId.Replace("{UnityEngine.AddressableAssets.Addressables.RuntimePath}", Addressables.BuildPath))
							: loc.InternalId;
						preferredCatalog.BuildPath = buildPath;
						preferredCatalog.Files.Add(filePath);
						preferredCatalog.BuildInfo.Locations.Add(new ContentCatalogDataEntry(typeof(IAssetBundleResource), internalId, loc.Provider, loc.Keys, loc.Dependencies, loc.Data));
					}
					else
					{
						preferredCatalog.BuildInfo.Locations.Add(loc);
					}
				}
				else
				{
					defaultCatalog.Locations.Add(loc);
				}
			}

			// Process dependencies
			foreach (CatalogSetup additionalCatalog in m_catalogSetups)
			{
				Queue<ContentCatalogDataEntry> dataEntries = new Queue<ContentCatalogDataEntry>(additionalCatalog.BuildInfo.Locations);
				HashSet<ContentCatalogDataEntry> processedEntries = new HashSet<ContentCatalogDataEntry>();
				while (dataEntries.Count > 0)
				{
					ContentCatalogDataEntry dataEntry = dataEntries.Dequeue();
					if (!processedEntries.Add(dataEntry) || (dataEntry.Dependencies == null) || (dataEntry.Dependencies.Count == 0))
					{
						continue;
					}

					foreach (object entryDependency in dataEntry.Dependencies)
					{
						// Search for the dependencies in the default catalog only.
						ContentCatalogDataEntry depLocation = defaultCatalog.Locations.Find(loc => loc.Keys[0] == entryDependency);
						if (depLocation != null)
						{
							dataEntries.Enqueue(depLocation);

							// If the dependency wasn't part of the catalog yet, add it.
							if (!additionalCatalog.BuildInfo.Locations.Contains(depLocation))
							{
								additionalCatalog.BuildInfo.Locations.Add(depLocation);
							}
						}
						else if (!additionalCatalog.BuildInfo.Locations.Exists(loc => loc.Keys[0] == entryDependency))
						{
							Debug.LogErrorFormat("Could not find location for dependency ID {0} in the default catalog.", entryDependency);
						}
					}
				}
			}

			// Gather catalogs
			List<ContentCatalogBuildInfo> catalogs = new List<ContentCatalogBuildInfo>(m_catalogSetups.Count + 1);

			if (addressableGroups.Length != 0)
			{
				catalogs.Add(defaultCatalog);
			}
			
			foreach (CatalogSetup catalogSetup in m_catalogSetups.Where(catalogSetup => !catalogSetup.Empty))
			{
				catalogs.Add(catalogSetup.BuildInfo);
				builtBundles.Add(Path.Combine(catalogSetup.BuildPath, catalogSetup.AddressableAssetGroup.Name));
			}
			return catalogs;
		}
		
		/// <summary>
		/// Returns the corresponding catalog setup for the location <paramref name="loc"/>
		/// </summary>
		/// <param name="loc">Location to get the catalog from</param>
		/// <param name="aaContext">Addressable context</param>
		private CatalogSetup GetCorrespondingCatalogSetup(ContentCatalogDataEntry loc, AddressableAssetsBuildContext aaContext)
		{
			foreach (CatalogSetup catalogSetup in m_catalogSetups.Where(catalogSetup => catalogSetup.AddressableAssetGroup != null))
			{
				//Special check for asset bundles
				if (loc.ResourceType == typeof(IAssetBundleResource))
				{
					AddressableAssetEntry entry = aaContext.assetEntries.Find(ae => string.Equals(ae.BundleFileId, loc.InternalId));
					
					if (entry != null)
					{
						if (catalogSetup.AddressableAssetGroup.entries.Contains(entry))
						{
							return catalogSetup;
						}
					}

					// If no entry was found, it may refer to a folder asset.
					if (catalogSetup.AddressableAssetGroup.entries.Any(e => e.IsFolder && e.BundleFileId.Equals(loc.InternalId)))
					{
						return catalogSetup;
					}
				}
				else
				{
					if (catalogSetup.AddressableAssetGroup.entries.Any(e => (e.IsFolder && e.SubAssets.Any(a => loc.Keys.Contains(a.guid))) || loc.Keys.Contains(e.guid)))
					{
						return catalogSetup;
					}
				}
			}

			return null;
		}

		public override void ClearCachedData()
		{
			base.ClearCachedData();

			if (builtBundles.Count == 0)
			{
				return;
			}
			
			//Deletes everything that is saved in the build bundles
			foreach (string buildPath in builtBundles)
			{
				string directory = Path.GetFullPath(Path.GetDirectoryName(buildPath));
				string buildPathName = Path.GetFileNameWithoutExtension(buildPath).ToLower();

				foreach (string file in Directory.GetFiles(directory))
				{
					string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
					
					//Need to make sure to delete everything that starts with the same name, special condition for spaces too
					if (fileName.StartsWith(buildPathName) || fileName.StartsWith(buildPathName.Replace(" ", "")))
					{
						File.Delete(file);
					}
				}
			}
			
			builtBundles.Clear();
		}

		#endregion Multi catalog section
		
		#region Data holders
		
		private class CatalogSetup
		{
			public readonly AddressableAssetGroup AddressableAssetGroup = null;

			/// <summary>
			/// The catalog build info.
			/// </summary>
			public readonly ContentCatalogBuildInfo BuildInfo;

			/// <summary>
			/// The files associated to the catalog.
			/// </summary>
			public readonly List<string> Files = new List<string>(1);

			/// <summary>
			/// Tells whether the catalog is empty.
			/// </summary>
			public bool Empty => BuildInfo.Locations.Count == 0;
			
			public string BuildPath { get; set; }

			public CatalogSetup(AddressableAssetGroup assetGroup, string identifier)
			{
				AddressableAssetGroup = assetGroup;
				BuildInfo = new ContentCatalogBuildInfo(identifier, assetGroup.Name + ".json")
				{
					Register = false
				};
			}

			public CatalogSetup(AddressableAssetGroup assetGroup, ContentCatalogBuildInfo buildInfo)
			{
				AddressableAssetGroup = assetGroup;
				BuildInfo = buildInfo;
				BuildInfo.Register = false;
			}
		}
		
		#endregion Data holders
	}
}
