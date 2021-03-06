﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Newtonsoft.Json;

namespace Nest
{
	[JsonConverter(typeof(PropertiesJsonConverter))]
	public interface IProperties : IIsADictionary<PropertyName, IProperty> { }

	public class Properties : IsADictionaryBase<PropertyName, IProperty>, IProperties
	{
		public Properties() : base() { }
		public Properties(IDictionary<PropertyName, IProperty> container) : base(container) { }
		public Properties(Dictionary<PropertyName, IProperty> container)
			: base(container.Select(kv => kv).ToDictionary(kv => kv.Key, kv => kv.Value))
		{ }

		public void Add(PropertyName name, IProperty property) => this.BackingDictionary.Add(name, property);
	}

	public class Properties<T> : IsADictionaryBase<PropertyName, IProperty>, IProperties
	{
		public Properties() : base() { }
		public Properties(IDictionary<PropertyName, IProperty> container) : base(container) { }
		public Properties(IProperties properties) : base(properties) { }
		public Properties(Dictionary<PropertyName, IProperty> container)
			: base(container.Select(kv => kv).ToDictionary(kv => kv.Key, kv => kv.Value))
		{ }

		public void Add(PropertyName name, IProperty property) => this.BackingDictionary.Add(name, property);
		public void Add(Expression<Func<T, object>> name, IProperty property) => this.BackingDictionary.Add(name, property);
	}

	public interface IPropertiesDescriptor<T, out TReturnType>
		where T : class
		where TReturnType : class
	{

		[Obsolete("Only valid for indices created before Elasticsearch 5.0 and will be removed in the next major version.  For newly created indices, use `text` or `keyword` instead.")]
		TReturnType String(Func<StringPropertyDescriptor<T>, IStringProperty> selector);
		TReturnType Text(Func<TextPropertyDescriptor<T>, ITextProperty> selector);
		TReturnType Keyword(Func<KeywordPropertyDescriptor<T>, IKeywordProperty> selector);
		TReturnType Number(Func<NumberPropertyDescriptor<T>, INumberProperty> selector);
		TReturnType TokenCount(Func<TokenCountPropertyDescriptor<T>, ITokenCountProperty> selector);
		TReturnType Date(Func<DatePropertyDescriptor<T>, IDateProperty> selector);
		TReturnType Boolean(Func<BooleanPropertyDescriptor<T>, IBooleanProperty> selector);
		TReturnType Binary(Func<BinaryPropertyDescriptor<T>, IBinaryProperty> selector);
		TReturnType Attachment(Func<AttachmentPropertyDescriptor<T>, IAttachmentProperty> selector);
		TReturnType Object<TChild>(Func<ObjectTypeDescriptor<T, TChild>, IObjectProperty> selector)
			where TChild : class;
		TReturnType Nested<TChild>(Func<NestedPropertyDescriptor<T, TChild>, INestedProperty> selector)
			where TChild : class;
		TReturnType Ip(Func<IpPropertyDescriptor<T>, IIpProperty> selector);
		TReturnType GeoPoint(Func<GeoPointPropertyDescriptor<T>, IGeoPointProperty> selector);
		TReturnType GeoShape(Func<GeoShapePropertyDescriptor<T>, IGeoShapeProperty> selector);
		TReturnType Completion(Func<CompletionPropertyDescriptor<T>, ICompletionProperty> selector);
		TReturnType Murmur3Hash(Func<Murmur3HashPropertyDescriptor<T>, IMurmur3HashProperty> selector);
		TReturnType Percolator(Func<PercolatorPropertyDescriptor<T>, IPercolatorProperty> selector);
	}

	public class PropertiesDescriptor<T> : IsADictionaryDescriptorBase<PropertiesDescriptor<T>, IProperties, PropertyName, IProperty>, IPropertiesDescriptor<T, PropertiesDescriptor<T>>
		where T : class
	{
		public PropertiesDescriptor() : base(new Properties<T>()) { }
		public PropertiesDescriptor(IProperties properties) : base(properties ?? new Properties<T>()) { }


		[Obsolete("Only valid for indices created before Elasticsearch 5.0 and will be removed in the next major version.  For newly created indices, use `text` or `keyword` instead.")]
		public PropertiesDescriptor<T> String(Func<StringPropertyDescriptor<T>, IStringProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Text(Func<TextPropertyDescriptor<T>, ITextProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Keyword(Func<KeywordPropertyDescriptor<T>, IKeywordProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Number(Func<NumberPropertyDescriptor<T>, INumberProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> TokenCount(Func<TokenCountPropertyDescriptor<T>, ITokenCountProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Date(Func<DatePropertyDescriptor<T>, IDateProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Boolean(Func<BooleanPropertyDescriptor<T>, IBooleanProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Binary(Func<BinaryPropertyDescriptor<T>, IBinaryProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Attachment(Func<AttachmentPropertyDescriptor<T>, IAttachmentProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Object<TChild>(Func<ObjectTypeDescriptor<T, TChild>, IObjectProperty> selector)
			where TChild : class => SetProperty(selector);

		public PropertiesDescriptor<T> Nested<TChild>(Func<NestedPropertyDescriptor<T, TChild>, INestedProperty> selector)
			where TChild : class => SetProperty(selector);

		public PropertiesDescriptor<T> Ip(Func<IpPropertyDescriptor<T>, IIpProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> GeoPoint(Func<GeoPointPropertyDescriptor<T>, IGeoPointProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> GeoShape(Func<GeoShapePropertyDescriptor<T>, IGeoShapeProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Completion(Func<CompletionPropertyDescriptor<T>, ICompletionProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Murmur3Hash(Func<Murmur3HashPropertyDescriptor<T>, IMurmur3HashProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Percolator(Func<PercolatorPropertyDescriptor<T>, IPercolatorProperty> selector) => SetProperty(selector);

		public PropertiesDescriptor<T> Custom(IProperty customType) => SetProperty(customType);

		private PropertiesDescriptor<T> SetProperty<TDescriptor, TInterface>(Func<TDescriptor, TInterface> selector)
			where TDescriptor : class, TInterface, new()
			where TInterface : IProperty
		{
			selector.ThrowIfNull(nameof(selector));
			var type = selector(new TDescriptor());
			SetProperty(type);
			return this;
		}

		private PropertiesDescriptor<T> SetProperty(IProperty type)
		{
			type.ThrowIfNull(nameof(type));
			var typeName = type.GetType().Name;
			if (type.Name.IsConditionless())
				throw new ArgumentException($"Could not get field name for {typeName} mapping");

			return this.Assign(a => a[type.Name] = type);
		}
	}

	internal static class PropertiesExtensions
	{
		internal static IProperties AutoMap<T>(this IProperties existingProperties, IPropertyVisitor visitor = null, int maxRecursion = 0)
			where T : class
		{
			var properties = new Properties();
			var autoProperties = new PropertyWalker(typeof(T), visitor, maxRecursion).GetProperties();
			foreach (var autoProperty in (IEnumerable<KeyValuePair<PropertyName, IProperty>>)autoProperties)
				properties[autoProperty.Key] = autoProperty.Value;

			// Existing/manually mapped properties always take precedence
			if (existingProperties != null)
			{
				foreach (var existing in (IEnumerable<KeyValuePair<PropertyName, IProperty>>)existingProperties)
					properties[existing.Key] = existing.Value;
			}

			return properties;
		}
	}
}
