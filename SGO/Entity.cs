﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Security;
using System.Reflection;
using System.Collections;
using Lidgren.Network;
using SS13_Shared;
using SS13_Shared.GO;
using System.Runtime.Serialization;
using ServerServices;
using ServerInterfaces;
using ServerInterfaces.MessageLogging;

namespace SGO
{
    /// <summary>
    /// Base entity class. Acts as a container for components, and a place to store location data.
    /// Should not contain any game logic whatsoever other than entity movement functions and 
    /// component management functions.
    /// </summary>
    [Serializable()]
    public class Entity : ISerializable
    {
        #region Variables
        /// <summary>
        /// Holds this entity's components
        /// </summary>
        private Dictionary<ComponentFamily, IGameObjectComponent> _components = new Dictionary<ComponentFamily, IGameObjectComponent>();
        
        private EntityNetworkManager m_entityNetworkManager;

        public EntityTemplate template;

        public event EntityMoveEvent OnMove;
        public delegate void EntityMoveEvent(Vector2 toPosition, Vector2 fromPosition);

        private bool _messageProfiling;

        private bool _initialized = false;

        public delegate void ShutdownEvent(Entity e);
        public event ShutdownEvent OnShutdown;

        private int uid;
        public int Uid
        {
            get
            {
                return uid;
            }
            set
            {
                uid = value;
            }
        }

        /// <summary>
        /// These are the only real pieces of data that the entity should have -- position and rotation.
        /// </summary>
        public Vector2 position;
        public float rotation;
        private string _name;
        public string Name
        {
            get { return _name; }
            set { 
                _name = value;
                SendNameUpdate();
            }
        }

        #endregion

        #region Constructor/Destructor
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="entityNetworkManager"></param>
        public Entity(EntityNetworkManager entityNetworkManager)
        {
            m_entityNetworkManager = entityNetworkManager;
            _messageProfiling = ServiceManager.Singleton.Resolve<IConfigManager>().MessageLogging;
        }

        /// <summary>
        /// Sets up variables and shite
        /// </summary>
        public void Initialize(bool loaded = false)
        {
            _initialized = true;
        }

        /// <summary>
        /// Shuts down the entity gracefully for removal.
        /// </summary>
        public void Shutdown()
        {
            foreach (GameObjectComponent component in _components.Values)
            {
                component.OnRemove();
            }
            _components.Clear();
        }
        #endregion

        #region Component Manipulation
        /// <summary>
        /// Public method to add a component to an entity.
        /// Calls the component's onAdd method, which also adds it to the component manager.
        /// </summary>
        /// <param name="family">the family of component -- there can only be one at a time per family.</param>
        /// <param name="component">The component.</param>
        public void AddComponent(ComponentFamily family, IGameObjectComponent component)
        {
            if (_components.Keys.Contains(family))
                RemoveComponent(family);
            _components.Add(family, component);
            component.OnAdd(this); 
        }

        /// <summary>
        /// Public method to remove a component from an entity.
        /// Calls the onRemove method of the component, which handles removing it 
        /// from the component manager and shutting down the component.
        /// </summary>
        /// <param name="family"></param>
        public void RemoveComponent(ComponentFamily family)
        {
            if (_components.Keys.Contains(family))
            {
                _components[family].OnRemove();
                _components.Remove(family); 
            }
        }

        /// <summary>
        /// Checks to see if a component of a certain family exists
        /// </summary>
        /// <param name="family">componentfamily to check</param>
        /// <returns>true if component exists, false otherwise</returns>
        public bool HasComponent(ComponentFamily family)
        {
            if (_components.ContainsKey(family))
                return true;
            return false;
        }

        /// <summary>
        /// Gets the component of the specified family, if it exists
        /// </summary>
        /// <param name="family">componentfamily to get</param>
        /// <returns></returns>
        public IGameObjectComponent GetComponent(ComponentFamily family)
        {
            if (_components.ContainsKey(family))
                return _components[family];
            return null;
        }

        /// <summary>
        /// Allows components to send messages
        /// </summary>
        /// <param name="sender">the component doing the sending</param>
        /// <param name="type">the type of message</param>
        /// <param name="args">message parameters</param>
        public void SendMessage(object sender, ComponentMessageType type, List<ComponentReplyMessage> replies, params object[] args)
        {
            LogComponentMessage(sender, type, args);

            foreach (var component in _components.Values.ToArray())
            {
                if (replies != null)
                {
                    var reply = component.RecieveMessage(sender, type, args);
                    if (reply.MessageType != ComponentMessageType.Empty)
                        replies.Add(reply);
                }
                else
                    component.RecieveMessage(sender, type, args);
            }
        }

        public void SendMessage(object sender, ComponentMessageType type, params object[] args)
        {
            LogComponentMessage(sender, type, args);

            foreach (var component in _components.Values.ToArray())
            {
                component.RecieveMessage(sender, type, args);
            }
        }

        public ComponentReplyMessage SendMessage(object sender, ComponentFamily family, ComponentMessageType type, params object[] args)
        {
            LogComponentMessage(sender, type, args);

            if (HasComponent(family))
                return GetComponent(family).RecieveMessage(sender, type, args);
            else
                return ComponentReplyMessage.Empty;
        }

        /// <summary>
        /// Logs a component message to the messaging profiler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="type"></param>
        /// <param name="args"></param>
        private void LogComponentMessage(object sender, ComponentMessageType type, params object[] args)
        {
            if (!_messageProfiling)
                return;
            var senderfamily = ComponentFamily.Generic;
            var uid = 0;
            var sendertype = "";
            //if (sender.GetType().IsAssignableFrom(typeof(IGameObjectComponent)))
            if (typeof(IGameObjectComponent).IsAssignableFrom(sender.GetType()))
            {
                var realsender = (IGameObjectComponent)sender;
                senderfamily = realsender.Family;

                uid = realsender.Owner.Uid;
                sendertype = realsender.GetType().ToString();
            }
            else
            {
                sendertype = sender.GetType().ToString();
            }
            //Log the message
            IMessageLogger logger = ServiceManager.Singleton.Resolve<IMessageLogger>();
            logger.LogComponentMessage(uid, senderfamily, sendertype, type);
        }
        #endregion

        public void Translate(Vector2 toPosition)
        {
            Vector2 oldPosition = position;
            position = toPosition;
            SendPositionUpdate();
            Moved(oldPosition);
        }

        public void Translate(Vector2 toPosition, float toRotation)
        {
            rotation = toRotation;
            Translate(toPosition);
        }

        #region Networking
        private void SendNameUpdate()
        {
            if (!_initialized)
                return;
            var message = m_entityNetworkManager.CreateEntityMessage();
            message.Write((int)EntityMessage.NameUpdate);
            message.Write(Name);
            m_entityNetworkManager.SendToAll(message);
        }        
        #endregion

        //VARIABLES TO REFACTOR AT A LATER DATE
        /// <summary>
        /// Movement speed of the entity. This should be refactored.
        /// </summary>
        public float speed = 6.0f;

        //FUNCTIONS TO REFACTOR AT A LATER DATE
        /// <summary>
        /// This should be refactored to some sort of component that sends entity movement input or something.
        /// </summary>
        public virtual void SendPositionUpdate()
        {
            SendMessage(this, ComponentMessageType.SendPositionUpdate);
        }

        public virtual void HandleClick(int clickerID) { }

        public void Moved(Vector2 fromPosition)
        {
            if(OnMove != null)
                OnMove(position, fromPosition);
        }

        #region Serialization

        public void SerializeBasicInfo(SerializationInfo info, StreamingContext ctxt)
        {
            Name = (string)info.GetValue("name", typeof(string));
            position = (Vector2)info.GetValue("position", typeof(Vector2));
            rotation = (float)info.GetValue("rotation", typeof(float));
        }

        public Entity(SerializationInfo info, StreamingContext ctxt)
        {
            Name = (string)info.GetValue("name", typeof(string));
            position = (Vector2)info.GetValue("position", typeof(Vector2));
            rotation = (float)info.GetValue("rotation", typeof(float));
        }

        public virtual void GetObjectData(SerializationInfo info, StreamingContext ctxt)
        {
            info.AddValue("name", Name);
            info.AddValue("position", position);
            info.AddValue("rotation", rotation);
        }

        #endregion

        internal void HandleNetworkMessage(IncomingEntityMessage message)
        {
            switch (message.messageType)
            {
                case EntityMessage.PositionMessage:
                    break;
                case EntityMessage.ComponentMessage:
                    HandleComponentMessage((IncomingEntityComponentMessage)message.message, message.client);
                    break;
                case EntityMessage.ComponentInstantiationMessage:
                    HandleComponentInstantiationMessage(message);
                    break;
            }
        }

        internal void HandleComponentInstantiationMessage(IncomingEntityMessage message)
        {
            if(HasComponent((ComponentFamily)message.message))
                GetComponent((ComponentFamily)message.message).HandleInstantiationMessage(message.client);
        }

        internal void HandleComponentMessage(IncomingEntityComponentMessage message, NetConnection client)
        {
            if (_components.Keys.Contains(message.componentFamily))
            {
                _components[message.componentFamily].HandleNetworkMessage(message, client);
            }
        }

        /// <summary>
        /// Sends a message to the counterpart component on the server side
        /// </summary>
        /// <param name="component">Sending component</param>
        /// <param name="method">Net Delivery Method</param>
        /// <param name="recipient">The intended recipient netconnection (if null send to all)</param>
        /// <param name="messageParams">Parameters</param>
        public void SendComponentNetworkMessage(IGameObjectComponent component, NetDeliveryMethod method, NetConnection recipient, params object[] messageParams)
        {
            if (!_initialized)
                return;
            m_entityNetworkManager.SendComponentNetworkMessage(this, component.Family, NetDeliveryMethod.ReliableUnordered, recipient, messageParams);
        }
    }
}
