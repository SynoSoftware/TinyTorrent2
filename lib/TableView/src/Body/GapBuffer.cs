using System.Collections;

namespace Syno.TableView.Body;

internal sealed class GapBuffer : IList<object>
{
    private object[] _buffer = Array.Empty<object>();
    private int _start;
    private int _end;

    public int Count => _buffer.Length - (_end - _start);
    public bool IsReadOnly => false;

    public object this[int index]
    {
        get => _buffer[Physical(index)];
        set => _buffer[Physical(index)] = value;
    }

    public void Insert(int index, object item)
    {
        if ((uint)index > (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        MoveGap(index);
        if (_start == _end)
        {
            object[] grown = new object[Math.Max(4, _buffer.Length * 2)];
            Array.Copy(_buffer, 0, grown, 0, _start);
            int end = grown.Length - (_buffer.Length - _end);
            Array.Copy(_buffer, _end, grown, end, _buffer.Length - _end);
            _buffer = grown;
            _end = end;
        }
        _buffer[_start++] = item;
    }

    public void RemoveAt(int index)
    {
        _ = Physical(index);
        MoveGap(index);
        _buffer[_end++] = null!;
    }

    private int Physical(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        return index < _start ? index : index + _end - _start;
    }

    private void MoveGap(int index)
    {
        int width = _end - _start;
        if (width == 0)
        {
            _start = index;
            _end = index;
            return;
        }
        if (index < _start)
        {
            Array.Copy(_buffer, index, _buffer, index + width, _start - index);
            Array.Clear(_buffer, index, Math.Min(_start - index, width));
        }
        else if (index > _start)
        {
            Array.Copy(_buffer, _end, _buffer, _start, index - _start);
            Array.Clear(_buffer, Math.Max(_end, index), Math.Min(index - _start, width));
        }
        _start = index;
        _end = index + width;
    }

    public void Add(object item) => Insert(Count, item);
    public void Clear()
    {
        Array.Clear(_buffer);
        _start = 0;
        _end = _buffer.Length;
    }

    public int IndexOf(object item)
    {
        for (int index = 0; index < Count; index++)
        {
            if (EqualityComparer<object>.Default.Equals(this[index], item))
            {
                return index;
            }
        }
        return -1;
    }

    public bool Contains(object item) => IndexOf(item) >= 0;
    public bool Remove(object item)
    {
        int index = IndexOf(item);
        if (index < 0)
        {
            return false;
        }
        RemoveAt(index);
        return true;
    }

    public void CopyTo(object[] array, int arrayIndex)
    {
        Array.Copy(_buffer, 0, array, arrayIndex, _start);
        Array.Copy(_buffer, _end, array, arrayIndex + _start, _buffer.Length - _end);
    }

    public IEnumerator<object> GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
