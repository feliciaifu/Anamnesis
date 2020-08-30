﻿// Concept Matrix 3.
// Licensed under the MIT license.

namespace Anamnesis.Memory.Marshalers
{
	using System;
	using Anamnesis.Memory.Offsets;

	internal class ColorMarshaler : MarshalerBase<Color>
	{
		public ColorMarshaler(params IMemoryOffset[] offsets)
			: base(offsets, 12)
		{
		}

		protected override Color Read(ref byte[] data)
		{
			Color value = default;
			value.R = BitConverter.ToSingle(data, 0);
			value.G = BitConverter.ToSingle(data, 4);
			value.B = BitConverter.ToSingle(data, 8);
			return value;
		}

		protected override void Write(Color value, ref byte[] data)
		{
			Array.Copy(BitConverter.GetBytes(value.R), data, 4);
			Array.Copy(BitConverter.GetBytes(value.G), 0, data, 4, 4);
			Array.Copy(BitConverter.GetBytes(value.B), 0, data, 8, 4);
		}
	}
}