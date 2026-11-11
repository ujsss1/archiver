// Обратимые преобразования данных перед сжатием.
// Сами по себе они ничего не сжимают, а переставляют байты так, чтобы модели было проще предсказывать.

using System.Buffers.Binary;

// ---------- Фильтр для EXE и DLL (x86) ----------
// Команды CALL (байт E8) и JMP (E9) хранят адрес перехода относительно текущего места.
// Вызов одной и той же функции из разных мест даёт разные байты, и модель не видит повторов.
// Фильтр заменяет относительный адрес на абсолютный: одинаковые вызовы становятся одинаковыми байтами.
static class X86Filter
{
    public static byte[] Encode(byte[] data)
    {
        return Transform(data, encode: true);
    }

    public static byte[] Decode(byte[] data)
    {
        return Transform(data, encode: false);
    }

    private static byte[] Transform(byte[] source, bool encode)
    {
        var data = (byte[])source.Clone();
        var index = 0;

        while (index + 4 < data.Length)
        {
            if ((data[index] & 0xFE) != 0xE8)
            {
                index++;
                continue;
            }

            // Берём только «правдоподобные» адреса: старший байт 00 или FF, то есть переход недалеко.
            // Это условие одинаково выполняется до и после преобразования.
            if (data[index + 4] == 0x00 || data[index + 4] == 0xFF)
            {
                var address = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(index + 1));
                var nextCommand = index + 5;

                address = encode ? address + nextCommand : address - nextCommand;
                address = (address << 7) >> 7; // оставляем 25 бит со знаком: старший байт снова 00 или FF

                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(index + 1), address);
            }

            // Перепрыгиваем все 5 байт команды, даже если адрес не меняли. Иначе следующий E8 внутри
            // этих байтов мог бы изменить их, и распаковка приняла бы решение по уже испорченным данным.
            index += 5;
        }

        return data;
    }
}

// ---------- Разбиение CSV на столбцы ----------
// В таблице соседние байты одной строки разнородны: дата, имя, цена, город.
// А в одном столбце данные похожи, поэтому храним таблицу «по столбцам»: сначала весь первый столбец, потом второй...
//
// Формат: [разделитель][1, если файл кончается переводом строки][число строк, 4 байта][число столбцов, 4 байта]
//         дальше столбцы подряд, каждое поле заканчивается '\n'.
static class CsvColumns
{
    private const int HeaderBytes = 10;

    // Возвращает null, если файл не похож на ровную таблицу (разное число полей в строках и т. п.)
    public static byte[]? TryEncode(byte[] data)
    {
        foreach (var separator in new[] { (byte)',', (byte)';', (byte)'\t' })
        {
            var encoded = TryEncode(data, separator);

            // Проверяем обратное преобразование сразу: если что-то не сошлось, просто не используем фильтр
            if (encoded != null && Decode(encoded).AsSpan().SequenceEqual(data))
            {
                return encoded;
            }
        }

        return null;
    }

    private static byte[]? TryEncode(byte[] data, byte separator)
    {
        var endsWithNewline = data.Length > 0 && data[^1] == '\n';
        ReadOnlySpan<byte> body = endsWithNewline ? data.AsSpan(0, data.Length - 1) : data.AsSpan();

        // Первый проход: считаем строки и проверяем, что во всех строках одинаковое число полей
        var rows = 0;
        var columns = -1;

        foreach (var line in body.Split((byte)'\n'))
        {
            var fields = body[line].Count(separator) + 1;

            if (columns < 0)
            {
                columns = fields;
            }
            else if (fields != columns)
            {
                return null;
            }

            rows++;
        }

        if (columns < 2 || rows < 2)
        {
            return null;
        }

        // Второй проход: запоминаем, где начинается каждое поле
        var fieldStarts = new int[rows * columns];
        var fieldEnds = new int[rows * columns];
        var row = 0;

        foreach (var line in body.Split((byte)'\n'))
        {
            var (lineStart, lineLength) = line.GetOffsetAndLength(body.Length);
            var column = 0;
            var fieldStart = lineStart;

            for (var i = lineStart; i <= lineStart + lineLength; i++)
            {
                if (i == lineStart + lineLength || body[i] == separator)
                {
                    fieldStarts[row * columns + column] = fieldStart;
                    fieldEnds[row * columns + column] = i;
                    column++;
                    fieldStart = i + 1;
                }
            }

            row++;
        }

        // Записываем столбцы один за другим
        var result = new byte[HeaderBytes + data.Length + 1];
        result[0] = separator;
        result[1] = (byte)(endsWithNewline ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(2), rows);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(6), columns);

        var size = HeaderBytes;

        for (var column = 0; column < columns; column++)
        {
            for (row = 0; row < rows; row++)
            {
                var start = fieldStarts[row * columns + column];
                var length = fieldEnds[row * columns + column] - start;

                body.Slice(start, length).CopyTo(result.AsSpan(size));
                size += length;
                result[size++] = (byte)'\n';
            }
        }

        Array.Resize(ref result, size);
        return result;
    }

    public static byte[] Decode(byte[] encoded)
    {
        var separator = encoded[0];
        var endsWithNewline = encoded[1] == 1;
        var rows = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(2));
        var columns = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(6));

        // Находим начало каждого поля в потоке столбцов
        var fieldStarts = new int[rows * columns];
        var fieldEnds = new int[rows * columns];
        var position = HeaderBytes;

        for (var column = 0; column < columns; column++)
        {
            for (var row = 0; row < rows; row++)
            {
                var end = encoded.AsSpan(position).IndexOf((byte)'\n') + position;
                fieldStarts[row * columns + column] = position;
                fieldEnds[row * columns + column] = end;
                position = end + 1;
            }
        }

        // Собираем строки обратно: поля через разделитель, строки через '\n'
        var result = new byte[encoded.Length];
        var size = 0;

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (column > 0)
                {
                    result[size++] = separator;
                }

                var start = fieldStarts[row * columns + column];
                var length = fieldEnds[row * columns + column] - start;
                encoded.AsSpan(start, length).CopyTo(result.AsSpan(size));
                size += length;
            }

            if (row < rows - 1 || endsWithNewline)
            {
                result[size++] = (byte)'\n';
            }
        }

        Array.Resize(ref result, size);
        return result;
    }
}
