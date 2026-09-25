using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PGvZOnlineMod.Core
{
    /// <summary>
    /// 网络实体注册表：netId ↔ 游戏对象 双向映射。
    /// - Host：首次在快照采集里见到某对象时分配递增 netId（懒分配，无需在每个生成点挂钩子）。
    /// - Client：收到 Spawn 事件创建对象时登记；收到 Retire 或本地死亡时解除。
    /// 禁止用 mPlants/mZombies 列表下标当 id（存档序列化按下标、删除即漂移）。
    /// </summary>
    public class NetIdRegistry
    {
        private class IdBox
        {
            public uint Id;
        }

        private readonly ConditionalWeakTable<object, IdBox> _idByObject = new();
        private readonly Dictionary<uint, object> _objectById = new();
        private uint _nextId = 1;

        /// <summary>分配（若已存在则返回原 id）。仅 Host 调用。</summary>
        public uint Assign(object obj)
        {
            if (_idByObject.TryGetValue(obj, out var box))
            {
                return box.Id;
            }
            uint id = _nextId++;
            // _nextId 溢出回绕时跳过 0（0 保留为"无效"）
            if (_nextId == 0)
            {
                _nextId = 1;
            }
            _idByObject.Add(obj, new IdBox { Id = id });
            _objectById[id] = obj;
            return id;
        }

        public bool TryGetId(object obj, out uint id)
        {
            if (_idByObject.TryGetValue(obj, out var box))
            {
                id = box.Id;
                return true;
            }
            id = 0;
            return false;
        }

        public bool TryGetObject(uint id, out object obj)
        {
            return _objectById.TryGetValue(id, out obj);
        }

        /// <summary>Client 端登记（远端下发已带 id）。</summary>
        public void Register(uint id, object obj)
        {
            if (id == 0 || obj == null)
            {
                return;
            }
            _idByObject.Remove(obj);
            _idByObject.Add(obj, new IdBox { Id = id });
            _objectById[id] = obj;
        }

        public void Remove(uint id)
        {
            if (_objectById.Remove(id, out var obj))
            {
                _idByObject.Remove(obj);
            }
        }

        public void Clear()
        {
            _objectById.Clear();
            _idByObject.Clear(); // .NET 6 的 ConditionalWeakTable 有 Clear()
            _nextId = 1;
        }
    }
}
