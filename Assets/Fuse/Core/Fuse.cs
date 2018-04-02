﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Fuse.Implementation;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Fuse.Core
{
	/// <summary>
	/// Executor for the framework. You should not be interacting with this.
	/// </summary>
	public class Fuse : SingletonBehaviour<Fuse>
	{
		private Environment _environment;
		private Configuration _configuration;
		private List<State> _allStates;
		private State _root;

		private List<StateTransition> _transitions;
		private Dictionary<string, List<ImplementationReference>> _implementations;
		private Dictionary<string, SceneReference> _scenes;

		private IEnumerator Start()
		{
			_transitions = new List<StateTransition>();
			_implementations = new Dictionary<string, List<ImplementationReference>>();
			_scenes = new Dictionary<string, SceneReference>();

#if DEVELOPMENT_BUILD || UNITY_EDITOR
			Logger.Enabled = true;
#else
			Logger.Enabled = false;
#endif

			Logger.Info("Starting ...");

			string localPath = string.Format(Constants.AssetsBakedPath, Constants.CoreBundleFile);
			yield return AssetBundles.LoadBundle(localPath, null, null, Logger.Exception);

			yield return AssetBundles.LoadAsset<Configuration>(
				Constants.GetConfigurationAssetPath(),
				result => { _configuration = result; },
				null,
				Logger.Exception
			);

			yield return AssetBundles.LoadAsset<Environment>(
				_configuration.Environment,
				result => { _environment = result; },
				null,
				Logger.Exception
			);

			Logger.Info("Loaded core");

			if (_environment.Loading == LoadMethod.Online)
			{
				yield return AssetBundles.UnloadBundle(Constants.CoreBundle, false);
				yield return AssetBundles.LoadBundle(_environment.GetUri(Constants.CoreBundleFile), -1, null, null,
					Logger.Exception);

				yield return AssetBundles.LoadAsset<Configuration>(
					Constants.GetConfigurationAssetPath(),
					result => { _configuration = result; },
					null,
					Logger.Exception
				);

				yield return AssetBundles.LoadAsset<Environment>(
					_configuration.Environment,
					result => { _environment = result; },
					null,
					Logger.Exception
				);

				Logger.Info("Updated core from remote source");
			}

			yield return AssetBundles.LoadAssets<State>(
				Constants.CoreBundle,
				result => { _allStates = result; },
				null,
				Logger.Exception);


			yield return SetState(_configuration.Start);

			Logger.Info("Started");
		}

		private void OnApplicationQuit()
		{
			AssetBundles.UnloadAllBundles(true);

			_transitions = null;
			_implementations = null;
			_configuration = null;
			_allStates = null;
			_root = null;

			Logger.Info("Stopped");
		}

		private IEnumerator SetState(string state)
		{
			if (string.IsNullOrEmpty(state))
			{
				Logger.Exception("You must have a valid initial state set in your " + typeof(Configuration).Name);
				yield break;
			}

			if (_root != null)
			{
				foreach (Implementation implementation in GetImplementations(_root))
					RemoveImplementationReference(implementation);

				foreach (string scenePath in GetScenes(_root))
					RemoveSceneReference(scenePath);
			}

			_root = GetState(state);

			_transitions.Clear();
			foreach (Transition transition in GetTransitions(_root))
				_transitions.Add(new StateTransition(transition));

			foreach (Implementation implementation in GetImplementations(_root))
				AddImplementationReference(implementation);

			foreach (string scenePath in GetScenes(_root))
				AddSceneReference(scenePath);

			foreach (KeyValuePair<string, List<ImplementationReference>> pair in _implementations)
			{
				foreach (ImplementationReference reference in pair.Value)
					if (!reference.Referenced)
						yield return CleanupImplementation(reference);
			}

			List<string> inactiveScenes = new List<string>();
			foreach (KeyValuePair<string, SceneReference> pair in _scenes)
			{
				yield return pair.Value.Process();
				if (!pair.Value.Active)
					inactiveScenes.Add(pair.Key);
			}

			inactiveScenes.ForEach(key => _scenes.Remove(key));

			foreach (KeyValuePair<string, List<ImplementationReference>> pair in _implementations)
			{
				foreach (ImplementationReference reference in pair.Value)
					if (!reference.Running)
						yield return SetupImplementation(reference);
			}
		}

		private State GetState(string state)
		{
			string[] values = state.Split(Constants.DefaultSeparator);
			string stateName = values[values.Length - 1].Replace(Constants.AssetExtension, string.Empty);
			return _allStates.Find(current => current.name == stateName);
		}

		private List<Transition> GetTransitions(State state)
		{
			List<Transition> result = new List<Transition>();

			while (state != null)
			{
				result.AddRange(state.Transitions);
				state = state.IsRoot ? null : GetState(state.Parent);
			}

			return result;
		}

		private List<string> GetScenes(State state)
		{
			List<string> result = new List<string>();

			while (state != null)
			{
				result.AddRange(state.Scenes);
				state = state.IsRoot ? null : GetState(state.Parent);
			}

			return result;
		}

		private List<Implementation> GetImplementations(State state)
		{
			List<Implementation> result = new List<Implementation>();

			while (state != null)
			{
				result.AddRange(state.Implementations.Select(implementationPath => new Implementation(implementationPath)));
				state = state.IsRoot ? null : GetState(state.Parent);
			}

			return result;
		}

		private void RemoveSceneReference(string scenePath)
		{
			_scenes[scenePath].RemoveReference();
		}

		private void AddSceneReference(string scenePath)
		{
			if (!_scenes.ContainsKey(scenePath))
				_scenes[scenePath] = new SceneReference(scenePath, _environment);

			_scenes[scenePath].AddReference();
		}

		private void RemoveImplementationReference(Implementation implementation)
		{
			GetReference(implementation).RemoveReference();
		}

		private void AddImplementationReference(Implementation implementation)
		{
			if (!_implementations.ContainsKey(implementation.Type))
				_implementations[implementation.Type] = new List<ImplementationReference>();

			ImplementationReference reference = GetReference(implementation);
			if (reference == null)
			{
				reference = new ImplementationReference(implementation, _environment, OnEventPublished, StartAsync);
				_implementations[implementation.Type].Add(reference);
			}

			reference.AddReference();
		}

		private ImplementationReference GetReference(Implementation implementation)
		{
			if (!_implementations.ContainsKey(implementation.Type))
				return null;

			return _implementations[implementation.Type].Find(current => current.Name == implementation.Name);
		}

		private IEnumerator SetupImplementation(ImplementationReference reference)
		{
			yield return reference.SetLifecycle(Lifecycle.Load);
			yield return reference.SetLifecycle(Lifecycle.Setup);
			yield return reference.SetLifecycle(Lifecycle.Active);
		}

		private IEnumerator CleanupImplementation(ImplementationReference reference)
		{
			yield return reference.SetLifecycle(Lifecycle.Cleanup);
			yield return reference.SetLifecycle(Lifecycle.Unload);

			_implementations[reference.Type].Remove(reference);
			if (_implementations[reference.Type].Count == 0)
				_implementations.Remove(reference.Type);
		}

		private void OnEventPublished(string type)
		{
			foreach (StateTransition transition in _transitions)
			{
				if (transition.ProcessEvent(type))
				{
					StartCoroutine(SetState(transition.State));
					break;
				}
			}
		}

		private void StartAsync(IEnumerator routine)
		{
			StartCoroutine(routine);
		}

		private class ImplementationReference
		{
			public bool Referenced
			{
				get { return _count > 0; }
			}

			public string Type
			{
				get { return _implementation.Type; }
			}

			public string Name
			{
				get { return _implementation.Name; }
			}

			public bool Running
			{
				get { return _lifecycle != Lifecycle.None; }
			}

			private uint _count;
			private Lifecycle _lifecycle;
			private Object _asset;

			private readonly Action<string> _notify;
			private readonly Action<IEnumerator> _async;
			private readonly Implementation _implementation;
			private readonly Environment _environment;

			public ImplementationReference(Implementation impl, Environment env, Action<string> notify,
				Action<IEnumerator> async)
			{
				_async = async;
				_environment = env;
				_notify = notify;
				_implementation = impl;
			}

			public void AddReference()
			{
				_count++;
			}

			public void RemoveReference()
			{
				_count--;
			}

			public IEnumerator SetLifecycle(Lifecycle lifecycle)
			{
				switch (lifecycle)
				{
					case Lifecycle.Load:
						yield return Load();
						ProcessListeners(true);
						ProcessInjections(lifecycle, _lifecycle);
						break;
					case Lifecycle.Setup:
					case Lifecycle.Active:
					case Lifecycle.Cleanup:
						ProcessInjections(lifecycle, _lifecycle);
						break;
					case Lifecycle.Unload:
						ProcessInjections(lifecycle, _lifecycle);
						ProcessListeners(false);
						AssetBundles.UnloadBundle(_implementation.Bundle, true);
						break;
				}

				_lifecycle = lifecycle;
			}

			private IEnumerator Load()
			{
				switch (_environment.Loading)
				{
					case LoadMethod.Baked:
						yield return AssetBundles.LoadBundle
						(
							_environment.GetPath(_implementation.BundleFile),
							null,
							null,
							Logger.Exception
						);
						break;
					case LoadMethod.Online:
						yield return AssetBundles.LoadBundle
						(
							_environment.GetUri(_implementation.BundleFile),
							_environment.GetVersion(_implementation.Bundle),
							null,
							null,
							Logger.Exception
						);
						break;
				}

				yield return AssetBundles.LoadAssets(
					_implementation.Bundle,
					System.Type.GetType(_implementation.Type, true, true),
					result => { _asset = result[0]; },
					null,
					Logger.Exception);
			}

			private void ProcessListeners(bool active)
			{
				foreach (MemberInfo member in _asset.GetType().GetMembers())
				{
					foreach (IFuseNotifier notifier in member.GetCustomAttributes(typeof(IFuseNotifier), true))
					{
						if (active)
							notifier.AddListener(_notify);
						else
							notifier.RemoveListener(_notify);
					}
				}
			}

			private void ProcessInjections(Lifecycle toEnter, Lifecycle toExit)
			{
				List<Pair<IFuseAttribute, MemberInfo>> attributes = new List<Pair<IFuseAttribute, MemberInfo>>();
				foreach (MemberInfo member in _asset.GetType().GetMembers())
				{
					foreach (object custom in member.GetCustomAttributes(typeof(IFuseAttribute), true))
						attributes.Add(new Pair<IFuseAttribute, MemberInfo>((IFuseAttribute) custom, member));
				}

				attributes.Sort((a, b) => a.A.Order < b.A.Order ? -1 : 1);

				foreach (Pair<IFuseAttribute, MemberInfo> attribute in attributes)
				{
					// we have a custom default value
					Lifecycle active = attribute.A.Lifecycle;
					if (active == Lifecycle.None)
						active = (Lifecycle) ((DefaultValueAttribute) active.GetType().GetCustomAttributes(true)[0]).Value;

					if (toEnter == active)
					{
						IFuseExecutor executor = attribute.A as IFuseExecutor;
						if (executor != null)
							executor.Execute(attribute.B, _asset);

						IFuseExecutorAsync executorAsync = attribute.A as IFuseExecutorAsync;
						if (executorAsync != null)
							_async(executorAsync.Execute(attribute.B, _asset));
					}

					if (toEnter == active || toExit == active)
					{
						IFuseLifecycle lifecycle = attribute.A as IFuseLifecycle;
						if (lifecycle != null)
						{
							if (toEnter == active)
								lifecycle.OnEnter(attribute.B, _asset);
							else if (toExit == active)
								lifecycle.OnExit(attribute.B, _asset);
						}
					}
				}
			}
		}

		private class SceneReference
		{
			public bool Active
			{
				get { return _loaded || _count > 0; }
			}

			private readonly string _path;
			private readonly Environment _environment;

			private uint _count;
			private bool _loaded;

			public SceneReference(string path, Environment environment)
			{
				_path = path;
				_environment = environment;
			}

			public void AddReference()
			{
				_count++;
			}

			public void RemoveReference()
			{
				_count--;
			}

			public IEnumerator Process()
			{
				if (_count == 0 && _loaded)
					yield return Unload();
				else if (_count > 0 && !_loaded)
					yield return Load();
			}

			private IEnumerator Load()
			{
				switch (_environment.Loading)
				{
					case LoadMethod.Baked:
						yield return AssetBundles.LoadBundle
						(
							_environment.GetPath(Constants.GetSceneBundleFileFromPath(_path)),
							null,
							null,
							Logger.Exception
						);
						break;
					case LoadMethod.Online:
						yield return AssetBundles.LoadBundle
						(
							_environment.GetUri(Constants.GetSceneBundleFileFromPath(_path)),
							_environment.GetVersion(Constants.GetSceneBundleFromPath(_path)),
							null,
							null,
							Logger.Exception
						);
						break;
				}

				yield return SceneManager.LoadSceneAsync(Constants.GetSceneNameFromPath(_path));

				_loaded = true;
			}

			private IEnumerator Unload()
			{
				yield return SceneManager.UnloadSceneAsync(Constants.GetSceneNameFromPath(_path));
				AssetBundles.UnloadBundle(Constants.GetSceneBundleFromPath(_path), true);
				_loaded = false;
			}
		}

		private class StateTransition
		{
			private readonly List<string> _events;

			public string State { get; private set; }

			public StateTransition(Transition transition)
			{
				State = transition.To;
				_events = transition.Events.ToList();
			}

			public bool ProcessEvent(string type)
			{
				int index = _events.IndexOf(type);
				if (index >= 0)
					_events.RemoveAt(index);
				return _events.Count == 0;
			}
		}
	}
}