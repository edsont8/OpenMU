﻿// <copyright file="PacketHandlerPlugInContainer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler
{
    using System;
    using System.Collections.Concurrent;
    using System.ComponentModel.Design;
    using System.Linq;
    using System.Reflection;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using MUnique.OpenMU.GameLogic;
    using MUnique.OpenMU.GameServer.RemoteView;
    using MUnique.OpenMU.Network.Packets;
    using MUnique.OpenMU.PlugIns;

    /// <summary>
    /// A plugin container which provides the effective packet handler plugins for the specified version.
    /// Base class for different kind of packet handler interfaces.
    /// </summary>
    /// <typeparam name="THandler">The type of the handler interface.</typeparam>
    public class PacketHandlerPlugInContainer<THandler> : StrategyPlugInProvider<byte, THandler>, IDisposable
        where THandler : class, IPacketHandlerPlugInBase
    {
        /// <summary>
        /// Since packet handler plugins are not holding any player state, they can be kept as singleton instances to save some memory.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, THandler> HandlerCache = new ConcurrentDictionary<Type, THandler>();

        private readonly IClientVersionProvider clientVersionProvider;

        private readonly ServiceContainer serviceContainer;
        private bool disposedValue;

        /// <summary>
        /// Initializes a new instance of the <see cref="PacketHandlerPlugInContainer{THandler}" /> class.
        /// </summary>
        /// <param name="clientVersionProvider">The client version provider.</param>
        /// <param name="manager">The manager.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public PacketHandlerPlugInContainer(IClientVersionProvider clientVersionProvider, PlugInManager manager, ILoggerFactory loggerFactory)
            : base(manager, loggerFactory)
        {
            this.clientVersionProvider = clientVersionProvider;
            this.clientVersionProvider.ClientVersionChanged += this.OnClientVersionChanged;
            this.serviceContainer = new ServiceContainer();
            this.serviceContainer.AddService(typeof(IClientVersionProvider), clientVersionProvider);
            this.serviceContainer.AddService(typeof(PlugInManager), manager);
            this.serviceContainer.AddService(typeof(ILoggerFactory), loggerFactory);
        }

        /// <summary>
        /// Initializes this instance.
        /// </summary>
        public void Initialize()
        {
            foreach (var plugInType in this.Manager.GetKnownPlugInsOf<THandler>().Where(this.Manager.IsPlugInActive))
            {
                this.BeforeActivatePlugInType(plugInType);
            }
        }

        /// <summary>
        /// Handles the incoming data packet by selecting the correct plugin.
        /// </summary>
        /// <param name="player">The player.</param>
        /// <param name="packet">The packet.</param>
        public void HandlePacket(RemotePlayer player, in Span<byte> packet)
        {
            var typeIndex = packet[0] % 2 == 1 ? 2 : 3;
            var packetType = packet[typeIndex];
            var handler = this[packetType];
            this.HandlePacket(player, packet, handler);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            this.Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    this.serviceContainer.Dispose();
                }

                this.disposedValue = true;
            }
        }

        /// <summary>
        /// Handles the incoming data packet by using the specified handler plugin.
        /// </summary>
        /// <param name="player">The player.</param>
        /// <param name="packet">The packet.</param>
        /// <param name="handler">The handler.</param>
        protected void HandlePacket(Player player, in Span<byte> packet, THandler? handler)
        {
            if (handler is null)
            {
                // unknown packet
                return;
            }

            if (handler.IsEncryptionExpected && (packet[0] < 0xC3))
            {
                this.Logger.LogWarning($"Packet was not encrypted and will not be handled: {packet.AsString()}");
                return;
            }

            handler.HandlePacket(player, packet);
        }

        /// <inheritdoc/>
        protected override void BeforeActivatePlugInType(Type plugInType)
        {
            base.BeforeActivatePlugInType(plugInType);

            var knownPlugIn = this.FindKnownPlugin(plugInType);
            if (knownPlugIn is null && this.clientVersionProvider.ClientVersion.IsPlugInSuitable(plugInType))
            {
                var plugIn = this.CreatePlugIn(plugInType);
                this.AddPlugIn(plugIn, true);
            }
        }

        /// <inheritdoc />
        protected override void ActivatePlugIn(THandler plugIn)
        {
            var newPlugInIsEffective = false;
            if (this.TryGetPlugIn(plugIn.Key, out var currentlyActivePlugIn))
            {
                if (currentlyActivePlugIn == plugIn)
                {
                    return;
                }

                if (currentlyActivePlugIn.IsPreferedTo(plugIn))
                {
                    return;
                }

                newPlugInIsEffective = true;
            }

            base.ActivatePlugIn(plugIn);
            if (newPlugInIsEffective)
            {
                this.SetEffectivePlugin(plugIn);
            }
        }

        /// <inheritdoc />
        protected override void DeactivatePlugIn(THandler plugIn)
        {
            var isEffectivePlugIn = this.TryGetPlugIn(plugIn.Key, out var currentlyActivePlugIn) && currentlyActivePlugIn == plugIn;
            base.DeactivatePlugIn(plugIn);

            if (!isEffectivePlugIn)
            {
                return;
            }

            // find available replacement if the plugin was effective before
            var replacement = this.ActivePlugIns
                .Where(p => p.Key == plugIn.Key)
                .OrderByDescending(p => p.GetType().GetCustomAttribute(typeof(MinimumClientAttribute)))
                .FirstOrDefault();
            if (replacement != null)
            {
                this.SetEffectivePlugin(replacement);
            }
        }

        private THandler CreatePlugIn(Type plugInType)
        {
            if (!HandlerCache.TryGetValue(plugInType, out var plugIn))
            {
                plugIn = (THandler)ActivatorUtilities.CreateInstance(this.serviceContainer, plugInType);
                if (plugInType.GetConstructors().Any(c => c.GetParameters().All(p => p.ParameterType != typeof(IClientVersionProvider))))
                {
                    HandlerCache.TryAdd(plugInType, plugIn);
                }

                if (plugIn is GroupPacketHandlerPlugIn groupPacketHandler)
                {
                    groupPacketHandler.Initialize();
                }
            }

            return plugIn;
        }

        private void OnClientVersionChanged(object? sender, EventArgs e)
        {
            this.Logger.LogWarning("Client version changed");

            foreach (var knownPlugIn in this.KnownPlugIns)
            {
                if (!this.clientVersionProvider.ClientVersion.IsPlugInSuitable(knownPlugIn.GetType()))
                {
                    this.DeactivatePlugIn(knownPlugIn);
                    this.RemovePlugIn(knownPlugIn);
                }
            }

            this.Initialize();
        }
    }
}