using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Rinten.Upgraders
{
	[UpgraderGroup( typeof( ReferenceTypeUpgraderGroup ) )]
	internal class ArrayUpgrader : Hotload.InstanceUpgrader
	{
		private readonly Dictionary<Type, bool> BlittableStructsCache = new Dictionary<Type, bool>();
		private readonly Dictionary<Type, bool> ChangedStructsCache = new Dictionary<Type, bool>();

		private SkipUpgrader SkipUpgrader;
		private AutoSkipUpgrader AutoSkipUpgrader;

		public override bool ShouldProcessType( Type type )
		{
			return type.IsArray;
		}

		protected override void OnInitialize()
		{
			SkipUpgrader = GetUpgrader<SkipUpgrader>();
			AutoSkipUpgrader = GetUpgraderOrDefault<AutoSkipUpgrader>();
		}

		protected override void OnClearCache()
		{
			//Clean up unmanaged allocations
			foreach ( var pair in ConverterCache )
			{
				pair.Value.Dispose();
			}

			ConverterCache.Clear();
			BlittableStructsCache.Clear();
			ChangedStructsCache.Clear();
		}

		private static Array CreateNewInstance( Type newElemType, Array oldArray )
		{
			switch ( oldArray.Rank )
			{
				case 1:
					return Array.CreateInstance( newElemType, oldArray.Length );

				default:
					var lengths = Enumerable.Range( 0, oldArray.Rank )
						.Select( oldArray.GetLength )
						.ToArray();

					return Array.CreateInstance( newElemType, lengths );
			}
		}

		protected override bool OnTryCreateNewInstance( object oldInstance, out object newInstance )
		{
			var oldElemType = oldInstance.GetType().GetElementType();
			var newElemType = GetNewType( oldElemType );
			var oldArray = (Array)oldInstance;

			newInstance = newElemType != oldElemType ? CreateNewInstance( newElemType, oldArray ) : oldArray;

			return true;
		}

		protected override bool OnTryUpgradeInstance( object oldInstance, object newInstance, bool createdElsewhere )
		{
			AddCachedInstance( oldInstance, newInstance );
			SuppressFinalize( oldInstance, newInstance );

			ScheduleProcessInstance( oldInstance, newInstance );

			return true;
		}

		protected override int OnProcessInstance( object oldInstance, object newInstance )
		{
			var oldType = oldInstance.GetType();
			var newType = newInstance.GetType();

			var oldArray = (Array)oldInstance;

			return ProcessArrayElements( oldArray, (Array)newInstance,
				oldType.GetElementType(), newType.GetElementType() );
		}

		public bool CanBlockCopy( Type oldElemType, Type newElemType )
		{
			return oldElemType.IsValueType && !oldElemType.IsPrimitive && !HasStructChanged( oldElemType, newElemType ?? oldElemType );
		}

		//True if a deep copy was required.
		private int ProcessArrayElements( Array oldInst, Array newInst, Type oldElemType, Type newElemType )
		{
			if ( ReferenceEquals( oldInst, newInst ) && CanSkipType( oldElemType ) ) return 1;

			if ( oldInst.Rank != newInst.Rank )
			{
				throw new NotImplementedException( "Rank change in in-place array upgrades not supported." );
			}

			for ( var i = 0; i < oldInst.Rank; ++i )
			{
				if ( oldInst.GetLowerBound( i ) != 0 )
				{
					throw new NotImplementedException( "Array with lower bound != 0 not supported." );
				}

				//Make sure there are actually any elements to process
				if ( oldInst.GetLength( i ) == 0 )
				{
					return 1;
				}
			}

			if ( CanBlockCopy( oldElemType, newElemType ) )
			{
				if ( newElemType == oldElemType && ReferenceEquals( oldInst, newInst ) )
				{
					//Type isn't defined in a hotloaded assembly, so can just use the old array
					return 1;
				}

				BlockCopyStructArray( oldInst, newInst, oldElemType, newElemType ?? oldElemType, oldInst.Length );
				return 1;
			}

			var oldPath = CurrentPath;

			if ( TracePaths )
			{
				CurrentPath = oldPath[typeof( int )];
			}

			try
			{
				switch ( oldInst.Rank )
				{
					case 1:
						{
							var length = Math.Min( oldInst.Length, newInst.Length );

							for ( var i = 0; i < length; ++i )
							{
								var oldValue = oldInst.GetValue( i );
								var newValue = GetNewInstance( oldValue );
								newInst.SetValue( newValue, i );
							}

							break;
						}

					default:
						{
							var indices = new int[oldInst.Rank];

							do
							{
								var oldValue = oldInst.GetValue( indices );
								var newValue = GetNewInstance( oldValue );
								newInst.SetValue( newValue, indices );
							} while ( IncrementIndices( oldInst, indices ) );

							break;
						}
				}

				return newInst.Length;
			}
			finally
			{
				CurrentPath = oldPath;
			}
		}

		private static bool IncrementIndices( Array array, int[] indices )
		{
			for ( var i = 0; i < array.Rank; ++i )
			{
				var length = array.GetLength( i );

				if ( ++indices[i] < length ) return true;

				indices[i] = 0;
			}

			return false;
		}

		private bool HasStructChangedUncached( Type oldType, Type newType )
		{
			try
			{
				var oldInfo = oldType.GetTypeInfo();
				var newInfo = newType.GetTypeInfo();

				if ( oldInfo.IsGenericType ) return true;
				if ( newInfo.IsGenericType ) return true;

				if ( oldInfo.IsEnum != newInfo.IsEnum ) return true;
				if ( oldInfo.IsEnum )
				{
					//TODO: we don't support block copying between enum types yet anyway, so this isn't needed

					var newUnderlyingType = Enum.GetUnderlyingType( newType );

					if ( Enum.GetUnderlyingType( oldType ) != newUnderlyingType ) return true;

					var oldNames = Enum.GetNames( oldType );
					var newNames = Enum.GetNames( newType );

					if ( oldNames.Length != newNames.Length ) return true;

					for ( var i = 0; i < oldNames.Length; ++i )
					{
						if ( oldNames[i] != newNames[i] ) return true;

						var oldValue = Convert.ChangeType( Enum.Parse( oldType, oldNames[i] ), newUnderlyingType );
						var newValue = Convert.ChangeType( Enum.Parse( newType, newNames[i] ), newUnderlyingType );

						if ( !oldValue.Equals( newValue ) ) return true;
					}

					return false;
				}

				if ( oldType.GetManagedSize() != newType.GetManagedSize() ) return true;

				const BindingFlags bFlags = BindingFlags.Public | BindingFlags.NonPublic
					| BindingFlags.Instance;

				var oldFields = oldType.GetFields( bFlags );
				var newFields = newType.GetFields( bFlags );

				if ( oldFields.Length != newFields.Length ) return true;

				for ( var i = 0; i < oldFields.Length; ++i )
				{
					var oldField = oldFields[i];
					var newField = newFields[i];

					if ( oldField.Name != newField.Name ) return true;
					if ( oldField.FieldType != newField.FieldType && GetNewType( oldField.FieldType ) != newField.FieldType ) return true;

					var typeInfo = newField.FieldType.GetTypeInfo();

					//May contain reference to a changed type.
					if ( typeInfo.IsByRef && !CanSkipType( newField.FieldType ) ) return true;

					//Recurse for inner structs.
					if ( typeInfo.IsValueType && !typeInfo.IsPrimitive && HasStructChanged( oldField.FieldType, newField.FieldType ) ) return true;
				}

				return false;
			}
			catch ( Exception e )
			{
				Log( HotloadEntryType.Error, $"Exception while checking struct type {oldType.FullName}: {e}" );

				//Safer to assume it has changed in the case of an error
				return true;
			}
		}

		private bool IsTypeBlittable( Type type )
		{
			if ( !type.IsValueType ) return false;
			if ( type.IsPrimitive ) return true;

			if ( !BlittableStructsCache.TryGetValue( type, out var isBlittable ) )
			{
				isBlittable = IsTypeBlittableUncached( type );
				BlittableStructsCache.Add( type, isBlittable );
			}

			return isBlittable;
		}

		//Determine if the size and field layout of a struct has changed. This should only return true if it is safe to bitwise copy
		//from old instances of the struct to new instances. This will return true if the struct contains reference-type members.
		private bool HasStructChanged( Type oldType, Type newType )
		{
			bool changed;
			if ( ChangedStructsCache.TryGetValue( oldType, out changed ) ) return changed;

			if ( !IsTypeBlittable( oldType ) )
				return true; //contains reference or something

			changed = HasStructChangedUncached( oldType, newType );
			ChangedStructsCache.Add( oldType, changed );

#if DEBUG
			Log( HotloadEntryType.Trace, $"HasStructChanged({oldType}): {changed}" );
#endif

			return changed;
		}

		private bool IsTypeBlittableUncached( Type type )
		{
			//TODO: we could probably eventually support block copying between enum types
			if ( type.IsEnum )
				return false;

			if ( type.IsPrimitive )
				return true;

			if ( type.IsClass || type.IsInterface )
				return false;

			foreach ( var field in type.GetRuntimeFields() )
			{
				if ( field.IsStatic ) continue;
				if ( !IsTypeBlittable( field.FieldType ) ) return false;
			}

			return true;
		}

		private readonly Dictionary<Type, StructArrayConverter> ConverterCache = new Dictionary<Type, StructArrayConverter>();

		public void BlockCopyStructArray( Array oldInst, Array newInst, Type oldElemType, Type newElemType, int count )
		{
			if ( oldElemType.IsEnum || newElemType.IsEnum )
			{
				throw new NotImplementedException( "Block copying enum arrays isn't supported yet." );
			}

			StructArrayConverter converter;
			if ( !ConverterCache.TryGetValue( oldElemType, out converter ) )
			{
				converter = StructArrayConverter.Create( oldElemType, newElemType );
				ConverterCache.Add( oldElemType, converter );
			}

			converter.BlockCopy( oldInst, newInst, count );
		}

		/// <summary>Return true if type is to be thought of as a primitive ie - a type that never changes, and can just be copied such as a bool, string, float, pointer.</summary>
		public bool CanSkipType( Type t )
		{
			var ti = t.GetTypeInfo();

			if ( ti.IsPrimitive ) return true;
			if ( ti.IsPointer ) return true;
			if ( t == typeof( string ) ) return true;
			if ( SkipUpgrader.ShouldProcessType( t ) ) return true;
			if ( t.IsSealed && (AutoSkipUpgrader?.ShouldProcessType( t ) ?? false) ) return true;

			return false;
		}
	}
}
