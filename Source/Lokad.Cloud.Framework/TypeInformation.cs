﻿#region Copyright (c) Lokad 2009
// This code is released under the terms of the new BSD licence.
// URL: http://www.lokad.com/
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.Specialized;

namespace Lokad.Cloud
{
	/// <summary>Contains information about a type.</summary>
	internal class TypeInformation
	{
		const string IsTransientKey = "IsTransient";
		const string ThrownOnDeserializationErrorKey = "ThrowOnDeserializationError";

		/// <summary>Gets a value indicating whether the type is transient.</summary>
		public bool IsTransient { get; private set; }

		/// <summary>If <see cref="IsTransient"/> is <c>true</c>, gets the behavior on deserialization error.</summary>
		public bool? ThrowOnDeserializationError { get; private set; }

		/// <summary>
		/// Gets the type information for a type.
		/// </summary>
		/// <param name="type">The type.</param>
		/// <returns>The type information.</returns>
		public static TypeInformation GetInformation(Type type)
		{
			var result = new TypeInformation() { ThrowOnDeserializationError = null };

			var transient = type.GetAttribute<TransientAttribute>(false);

			result.IsTransient = transient != null;
			if(result.IsTransient) result.ThrowOnDeserializationError = transient.ThrowOnDeserializationError;
			return result;
		}

		/// <summary>
		/// Saves the type information in blob metadata.
		/// </summary>
		/// <param name="metadata">The metadata to act on.</param>
		public void SaveInBlobMetadata(NameValueCollection metadata)
		{
			metadata.Remove(ThrownOnDeserializationErrorKey);
			metadata[IsTransientKey] = IsTransient.ToString();

			if(IsTransient)
			{
				metadata[ThrownOnDeserializationErrorKey] = ThrowOnDeserializationError.Value.ToString();
			}
		}

		/// <summary>
		/// Loads the type information from blob metadata.
		/// </summary>
		/// <param name="metadata">The metadata to act on.</param>
		/// <returns>The type information, if available, <c>null</c> otherwise.</returns>
		public static TypeInformation LoadFromBlobMetadata(NameValueCollection metadata)
		{
			string transientString = metadata[IsTransientKey];
			if(string.IsNullOrEmpty(transientString)) return null;

			bool isTransient = bool.Parse(transientString);
			TypeInformation result = new TypeInformation() { IsTransient = isTransient };

			if(isTransient)
			{
				result.ThrowOnDeserializationError = bool.Parse(metadata[ThrownOnDeserializationErrorKey]);
			}

			return result;
		}

		/// <summary>Determines whether the specified <see cref="T:System.Object"/>
		/// is equal to the current <see cref="T:System.Object"/>.</summary>
		/// <param name="obj">The <see cref="T:System.Object"/> to compare with the current <see cref="T:System.Object"/>.</param>
		/// <returns>true if the specified <see cref="T:System.Object"/> is equal to the current <see cref="T:System.Object"/>; otherwise, false.</returns>
		/// <exception cref="T:System.NullReferenceException">The <paramref name="obj"/> parameter is null.</exception>
		public override bool Equals(object obj)
		{
			if(object.ReferenceEquals(obj, null)) throw new NullReferenceException();

			TypeInformation realType = obj as TypeInformation;

			if(object.ReferenceEquals(realType, null)) return false;
			else return Equals(realType);
		}

		/// <summary>
		/// Equalses the specified info.
		/// </summary>
		/// <param name="info">The info.</param>
		/// <returns><c>true</c> if <paramref name="info"/> is equal to the current instance, <c>false</c> otherwise.</returns>
		public bool Equals(TypeInformation info)
		{
			if(object.ReferenceEquals(info, null)) throw new NullReferenceException();

			return
				info.IsTransient == IsTransient &&
				info.ThrowOnDeserializationError == ThrowOnDeserializationError;
		}

		/// <summary>Serves as a hash function for a particular type.</summary>
		/// <returns>A hash code for the current <see cref="T:System.Object"/>.</returns>
		public override int GetHashCode()
		{
			return base.GetHashCode();
		}
	}
}