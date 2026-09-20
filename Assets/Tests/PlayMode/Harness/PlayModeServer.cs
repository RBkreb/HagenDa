using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

namespace HagenDa.Tests.PlayMode.Harness
{
    /// <summary>
    /// 无端口的测试传输层。
    ///
    /// 为什么需要它：<c>NetworkServer.Listen()</c> 会在 <c>Initialize()</c> 里断言并解引用
    /// <c>Transport.active</c>（挂接 OnServerConnected 等事件），因此即使
    /// <c>NetworkServer.listen = false</c> 也必须有一个 Transport 实例存在。
    /// 测试不应绑定真实端口（CI/并行会冲突），故这里提供一个什么都不做的实现：
    /// 所有收发都是无操作，只保留 Mirror 需要的事件字段。
    /// </summary>
    public class NullTransport : Transport
    {
        public override bool Available() => true;

        // ---- client ----
        public override bool ClientConnected() => false;
        public override void ClientConnect(string address) { }
        public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable) { }
        public override void ClientDisconnect() { }

        // ---- server ----
        public override Uri ServerUri() => new Uri("null://localhost");
        public override bool ServerActive() => true;
        public override void ServerStart() { }
        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable) { }
        public override void ServerDisconnect(int connectionId) { }
        public override string ServerGetClientAddress(int connectionId) => "null";
        public override void ServerStop() { }

        public override int GetMaxPacketSize(int channelId = Channels.Reliable) => 1200;

        public override void Shutdown() { }
    }

    /// <summary>
    /// PlayMode 测试的服务器生命周期宿主。
    ///
    /// 提供三件事：
    /// 1. <see cref="StartServer"/> —— 启动一个**无端口**的 Mirror 服务端（不建 NetworkManager、
    ///    不连客户端），这是让 <c>[Server]</c> 方法与 <c>isServer</c> 生效的最小条件。
    /// 2. <see cref="Spawn"/> / <see cref="CreateServerObject{T}"/> —— 造出已生成的测试实体。
    /// 3. <see cref="StopServer"/> —— 彻底还原静态状态（Mirror 的 <c>Shutdown()</c> 会把
    ///    <c>listen</c> 重置为 true，必须重新压回 false，否则下一轮会尝试绑端口）。
    ///
    /// 关键语义（已实测确认，决定了本 harness 的形态）：
    /// - <c>[Server]</c> 守卫检查的是**全局** <c>NetworkServer.active</c>；未启动服务端时
    ///   <c>SetServerInput</c>/<c>ApplyDamage</c> 等会静默 no-op（只打 warning）。
    /// - <c>isServer</c> 是**按对象**的，只有真正 <c>NetworkServer.Spawn</c> 过才为 true；
    ///   而 <c>SimulateServer</c> 只在 <c>if (isServer)</c> 内运行。二者缺一不可。
    /// </summary>
    public static class PlayModeServer
    {
        private static GameObject _transportGo;

        /// <summary>脏状态标记：Shutdown 会把 listen 复位为 true，故本类自行记账。</summary>
        private static bool _listenSuppressed;

        public static bool IsRunning => NetworkServer.active;

        /// <summary>
        /// 启动无端口服务端。若已在运行则直接返回（重复调用安全）。
        /// </summary>
        public static void StartServer()
        {
            if (NetworkServer.active) return;

            EnsureTransport();
            SuppressPortBinding();

            NetworkServer.Listen(1);
        }

        private static void EnsureTransport()
        {
            if (Transport.active != null) return;

            _transportGo = new GameObject("[TestTransport]");
            UnityEngine.Object.DontDestroyOnLoad(_transportGo);
            var t = _transportGo.AddComponent<NullTransport>();
            Transport.active = t;
        }

        private static void SuppressPortBinding()
        {
            // listen=false 时 Listen() 跳过 Transport.ServerStart()，从而不绑端口。
            NetworkServer.listen = false;
            _listenSuppressed = true;
        }

        /// <summary>把一个运行时创建的 GameObject 生成到服务端（赋 netId、isServer=true）。</summary>
        public static void Spawn(GameObject go)
        {
            if (!NetworkServer.active)
                throw new InvalidOperationException("必须先 StartServer() 才能 Spawn。");

            // 先失活再生成：Mirror 要求 Instantiate 出来的对象先失活
            //（NetworkIdentity.Awake 在失活态下初始化各 NetworkBehaviour）。
            go.SetActive(false);
            NetworkServer.Spawn(go);
        }

        /// <summary>
        /// 创建并生成一个带 <see cref="NetworkIdentity"/> 的测试实体，返回其组件 T。
        ///
        /// **必须先失活再挂组件**：<c>new GameObject()</c> 默认是激活的，若先 AddComponent
        /// <see cref="NetworkIdentity"/>，它的 <c>Awake</c> 会立刻在"只有 identity"的状态下运行，
        /// 把 <c>NetworkBehaviours</c> 缓存成空表；之后 <c>OnStartServer</c> 遍历这张空表，
        /// 于是**本组件的 OnStartServer 永远不会被调用**（表现为 Instance 未设置、
        /// SyncVar 未初始化等，且毫无报错）。这与 <see cref="PlayerFixture"/> 里的顺序要求同源。
        /// </summary>
        public static T SpawnComponent<T>(string name = null) where T : Component
        {
            var go = new GameObject(name ?? typeof(T).Name);

            // 关键：失活态下装配，最后交给 Spawn 激活。
            go.SetActive(false);
            go.AddComponent<NetworkIdentity>();
            var component = go.AddComponent<T>();

            Spawn(go);
            return component;
        }

        /// <summary>
        /// 创建一个**不生成**的普通对象（用于 staging，例如只当靶子/靠 <c>[Server]</c> 全局守卫生效的对象）。
        /// </summary>
        public static T CreateUnspawned<T>(string name = null) where T : Component
        {
            var go = new GameObject(name ?? typeof(T).Name);
            return go.AddComponent<T>();
        }

        /// <summary>
        /// 销毁对象。只有真正带 <see cref="NetworkIdentity"/> 的对象才走 Mirror 的销毁路径
        ///（否则 <c>NetworkServer.Destroy</c> 会报 "doesn't have NetworkIdentity" 错误日志，
        /// 而 PlayMode 测试会把任何未预期的错误日志判为失败）。
        /// </summary>
        public static void Destroy(GameObject go)
        {
            if (go == null) return;

            bool mirrored = go.GetComponent<NetworkIdentity>() != null;
            if (NetworkServer.active && mirrored)
                NetworkServer.Destroy(go);
            else
                UnityEngine.Object.DestroyImmediate(go);
        }

        /// <summary>
        /// 彻底关闭并还原静态状态。测试 TearDown 必须调用。
        /// </summary>
        public static void StopServer()
        {
            if (NetworkServer.active)
                NetworkServer.Shutdown();

            // Shutdown() 内部把 listen 复位成 true；若下一轮直接 Listen 就会真的绑端口。
            if (_listenSuppressed)
            {
                NetworkServer.listen = false;
            }

            // 清掉可能在 EditMode/上一轮遗留的 transport。
            if (_transportGo != null)
            {
                UnityEngine.Object.DestroyImmediate(_transportGo);
                _transportGo = null;
            }
            Transport.active = null;

            // 静态单例也要还原，避免跨测试串味。
            NullOutSingleton("HagenDa.Networking.NetworkMatchManager", "Instance");
            NullOutSingleton("HagenDa.Networking.NetworkRoomController", "Instance");
        }

        private static void NullOutSingleton(string typeName, string propertyName)
        {
            var t = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefaultType(typeName);
            if (t == null) return;

            var p = t.GetProperty(propertyName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (p != null && p.CanWrite)
                p.SetValue(null, null);
        }
    }

    internal static class AssemblyLookup
    {
        public static Type FirstOrDefaultType(this IEnumerable<System.Reflection.Assembly> assemblies, string fullName)
        {
            foreach (var a in assemblies)
            {
                var t = a.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
