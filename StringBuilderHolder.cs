using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace MockGenereator
{
	internal static class StringBuilderHolder
	{
		static readonly ConcurrentBag<StringBuilder> _instances = new();

		class Disposable : IDisposable
		{
			public StringBuilder _instance;

			public void Dispose()
			{
				if (_instance != null)
				{
					Return(_instance);
					_instance = null;
				}
			}
		}

		public static IDisposable Get(out StringBuilder result)
		{
			if (_instances.TryTake(out result))
			{
				result.Clear();
			}
			else
			{
				result = new StringBuilder();
			}
			return new Disposable
			{
				_instance = result,
			};
		}

		public static void Return(StringBuilder instance)
		{
			_instances.Add(instance);
		}
	}
}
