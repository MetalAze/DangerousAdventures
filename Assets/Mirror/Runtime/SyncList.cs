using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;

namespace Mirror
{
    public class SyncListString : SyncList<string>
    {
        protected override void SerializeItem(NetworkWriter writer, string item)
        {
            writer.WriteString(item);
        }

        protected override string DeserializeItem(NetworkReader reader)
        {
            return reader.ReadString();
        }
    }

    public class SyncListFloat : SyncList<float>
    {
        protected override void SerializeItem(NetworkWriter writer, float item)
        {
            writer.WriteSingle(item);
        }

        protected override float DeserializeItem(NetworkReader reader)
        {
            return reader.ReadSingle();
        }
    }

    public class SyncListInt : SyncList<int>
    {
        protected override void SerializeItem(NetworkWriter writer, int item)
        {
            writer.WritePackedInt32(item);
        }

        protected override int DeserializeItem(NetworkReader reader)
        {
            return reader.ReadPackedInt32();
        }
    }

    public class SyncListUInt : SyncList<uint>
    {
        protected override void SerializeItem(NetworkWriter writer, uint item)
        {
            writer.WritePackedUInt32(item);
        }

        protected override uint DeserializeItem(NetworkReader reader)
        {
            return reader.ReadPackedUInt32();
        }
    }

    public class SyncListBool : SyncList<bool>
    {
        protected override void SerializeItem(NetworkWriter writer, bool item)
        {
            writer.WriteBoolean(item);
        }

        protected override bool DeserializeItem(NetworkReader reader)
        {
            return reader.ReadBoolean();
        }
    }

    // Original UNET name is SyncListStruct and original Weaver weavers anything
    // that contains the name 'SyncListStruct', without considering the name-
    // space.
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Use SyncList<MyStruct> instead")]
    public class SyncListSTRUCT<T> : SyncList<T> where T : struct
    {
        public T GetItem(int i)
        {
            return base[i];
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract class SyncList<T> : IList<T>, IReadOnlyList<T>, SyncObject
    {
        public delegate void SyncListChanged(Operation op, int itemIndex, T oldItem, T newItem);

        private readonly IList<T> objects;
        private readonly IEqualityComparer<T> comparer;

        public int Count => objects.Count;
        public bool IsReadOnly { get; private set; }
        public event SyncListChanged Callback;

        public enum Operation : byte
        {
            OP_ADD,
            OP_CLEAR,
            OP_INSERT,

            [Obsolete("Lists now pass OP_REMOVEAT")]
            OP_REMOVE,
            OP_REMOVEAT,
            OP_SET,

            [Obsolete("Lists now use OP_SET instead of OP_DIRTY")]
            OP_DIRTY
        }

        private struct Change
        {
            internal Operation operation;
            internal int index;
            internal T item;
        }

        private readonly List<Change> changes = new List<Change>();

        // how many changes we need to ignore
        // this is needed because when we initialize the list,
        // we might later receive changes that have already been applied
        // so we need to skip them
        private int changesAhead;

        protected virtual void SerializeItem(NetworkWriter writer, T item)
        {
        }

        protected virtual T DeserializeItem(NetworkReader reader)
        {
            return default;
        }

        protected SyncList(IEqualityComparer<T> comparer = null)
        {
            this.comparer = comparer ?? EqualityComparer<T>.Default;
            objects = new List<T>();
        }

        protected SyncList(IList<T> objects, IEqualityComparer<T> comparer = null)
        {
            this.comparer = comparer ?? EqualityComparer<T>.Default;
            this.objects = objects;
        }

        public bool IsDirty => changes.Count > 0;

        // throw away all the changes
        // this should be called after a successfull sync
        public void Flush()
        {
            changes.Clear();
        }

        private void AddOperation(Operation op, int itemIndex, T oldItem, T newItem)
        {
            if (IsReadOnly) throw new InvalidOperationException("Synclists can only be modified at the server");

            var change = new Change
            {
                operation = op,
                index = itemIndex,
                item = newItem
            };

            changes.Add(change);

            Callback?.Invoke(op, itemIndex, oldItem, newItem);
        }

        public void OnSerializeAll(NetworkWriter writer)
        {
            // if init,  write the full list content
            writer.WritePackedUInt32((uint) objects.Count);

            for (var i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                SerializeItem(writer, obj);
            }

            // all changes have been applied already
            // thus the client will need to skip all the pending changes
            // or they would be applied again.
            // So we write how many changes are pending
            writer.WritePackedUInt32((uint) changes.Count);
        }

        public void OnSerializeDelta(NetworkWriter writer)
        {
            // write all the queued up changes
            writer.WritePackedUInt32((uint) changes.Count);

            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                writer.WriteByte((byte) change.operation);

                switch (change.operation)
                {
                    case Operation.OP_ADD:
                        SerializeItem(writer, change.item);
                        break;

                    case Operation.OP_CLEAR:
                        break;

                    case Operation.OP_REMOVEAT:
                        writer.WritePackedUInt32((uint) change.index);
                        break;

                    case Operation.OP_INSERT:
                    case Operation.OP_SET:
                        writer.WritePackedUInt32((uint) change.index);
                        SerializeItem(writer, change.item);
                        break;
                }
            }
        }

        public void OnDeserializeAll(NetworkReader reader)
        {
            // This list can now only be modified by synchronization
            IsReadOnly = true;

            // if init,  write the full list content
            var count = (int) reader.ReadPackedUInt32();

            objects.Clear();
            changes.Clear();

            for (var i = 0; i < count; i++)
            {
                var obj = DeserializeItem(reader);
                objects.Add(obj);
            }

            // We will need to skip all these changes
            // the next time the list is synchronized
            // because they have already been applied
            changesAhead = (int) reader.ReadPackedUInt32();
        }

        public void OnDeserializeDelta(NetworkReader reader)
        {
            // This list can now only be modified by synchronization
            IsReadOnly = true;

            var changesCount = (int) reader.ReadPackedUInt32();

            for (var i = 0; i < changesCount; i++)
            {
                var operation = (Operation) reader.ReadByte();

                // apply the operation only if it is a new change
                // that we have not applied yet
                var apply = changesAhead == 0;
                var index = 0;
                T oldItem = default;
                T newItem = default;

                switch (operation)
                {
                    case Operation.OP_ADD:
                        newItem = DeserializeItem(reader);
                        if (apply)
                        {
                            index = objects.Count;
                            objects.Add(newItem);
                        }

                        break;

                    case Operation.OP_CLEAR:
                        if (apply) objects.Clear();
                        break;

                    case Operation.OP_INSERT:
                        index = (int) reader.ReadPackedUInt32();
                        newItem = DeserializeItem(reader);
                        if (apply) objects.Insert(index, newItem);
                        break;

                    case Operation.OP_REMOVEAT:
                        index = (int) reader.ReadPackedUInt32();
                        if (apply)
                        {
                            oldItem = objects[index];
                            objects.RemoveAt(index);
                        }

                        break;

                    case Operation.OP_SET:
                        index = (int) reader.ReadPackedUInt32();
                        newItem = DeserializeItem(reader);
                        if (apply)
                        {
                            oldItem = objects[index];
                            objects[index] = newItem;
                        }

                        break;
                }

                if (apply)
                    Callback?.Invoke(operation, index, oldItem, newItem);
                // we just skipped this change
                else
                    changesAhead--;
            }
        }

        public void Add(T item)
        {
            objects.Add(item);
            AddOperation(Operation.OP_ADD, objects.Count - 1, default, item);
        }

        public void Clear()
        {
            objects.Clear();
            AddOperation(Operation.OP_CLEAR, 0, default, default);
        }

        public bool Contains(T item)
        {
            return IndexOf(item) >= 0;
        }

        public void CopyTo(T[] array, int index)
        {
            objects.CopyTo(array, index);
        }

        public int IndexOf(T item)
        {
            for (var i = 0; i < objects.Count; ++i)
                if (comparer.Equals(item, objects[i]))
                    return i;
            return -1;
        }

        public int FindIndex(Predicate<T> match)
        {
            for (var i = 0; i < objects.Count; ++i)
                if (match(objects[i]))
                    return i;
            return -1;
        }

        public void Insert(int index, T item)
        {
            objects.Insert(index, item);
            AddOperation(Operation.OP_INSERT, index, default, item);
        }

        public bool Remove(T item)
        {
            var index = IndexOf(item);
            var result = index >= 0;
            if (result) RemoveAt(index);
            return result;
        }

        public void RemoveAt(int index)
        {
            var oldItem = objects[index];
            objects.RemoveAt(index);
            AddOperation(Operation.OP_REMOVEAT, index, oldItem, default);
        }

        public T this[int i]
        {
            get => objects[i];
            set
            {
                if (!comparer.Equals(objects[i], value))
                {
                    var oldItem = objects[i];
                    objects[i] = value;
                    AddOperation(Operation.OP_SET, i, oldItem, value);
                }
            }
        }

        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return new Enumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new Enumerator(this);
        }

        // default Enumerator allocates. we need a custom struct Enumerator to
        // not allocate on the heap.
        // (System.Collections.Generic.List<T> source code does the same)
        //
        // benchmark:
        //   uMMORPG with 800 monsters, Skills.GetHealthBonus() which runs a
        //   foreach on skills SyncList:
        //      before: 81.2KB GC per frame
        //      after:     0KB GC per frame
        // => this is extremely important for MMO scale networking
        public struct Enumerator : IEnumerator<T>
        {
            private readonly SyncList<T> list;
            private int index;
            public T Current { get; private set; }

            public Enumerator(SyncList<T> list)
            {
                this.list = list;
                index = -1;
                Current = default;
            }

            public bool MoveNext()
            {
                if (++index >= list.Count) return false;
                Current = list[index];
                return true;
            }

            public void Reset()
            {
                index = -1;
            }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }
        }
    }
}