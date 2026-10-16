namespace Rinten
{
	public partial struct PhysicsTraceBuilder
	{
		internal unsafe PhysicsTraceBuilder WithOptionalTag( StringToken tag )
		{
			for ( int i = 0; i < PhysicsTrace.Request.NumTagFields; i++ )
			{
				if ( request.TagAny[i] == tag.Value ) return this;

				if ( request.TagAny[i] == 0 )
				{
					request.TagAny[i] = tag.Value;
					return this;
				}
			}

			return this;
		}

		/// <summary>Only return entities with this tag. Subsequent calls to this will add multiple requirements and they'll all have to be met (ie, the entity will need all tags).</summary>
		public unsafe PhysicsTraceBuilder WithTag( StringToken ident )
		{
			for ( int i = 0; i < PhysicsTrace.Request.NumTagFields; i++ )
			{
				if ( request.TagRequire[i] == ident.Value ) return this;

				if ( request.TagRequire[i] == 0 )
				{
					request.TagRequire[i] = ident.Value;
					return this;
				}
			}

			return this;
		}

		/// <summary>Only return with all of these tags</summary>
		public unsafe PhysicsTraceBuilder WithAllTags( params string[] tags )
		{
			var t = this;

			foreach ( var tag in tags )
			{
				t = t.WithTag( tag );
			}

			return t;
		}

		/// <summary>Only return with all of these tags</summary>
		public unsafe PhysicsTraceBuilder WithAllTags( ITagSet tags )
		{
			if ( tags is null ) return this;

			var t = this;

			foreach ( var token in ITagSet.Copy( tags.GetTokens() ) )
			{
				t = t.WithTag( new StringToken( token ) );
			}

			return t;
		}

		/// <summary>Only return entities with any of these tags</summary>
		public unsafe PhysicsTraceBuilder WithAnyTags( params string[] tags )
		{
			var t = this;

			foreach ( var tag in tags )
			{
				t = t.WithOptionalTag( tag );
			}

			return t;
		}

		//Only return entities with any of these tags (using ints)
		internal unsafe PhysicsTraceBuilder WithAnyTags( IReadOnlySet<uint> tags )
		{
			var t = this;

			//fast path, can just copy over
			if ( request.TagAny[0] == 0 )
			{
				var first = ITagSet.Copy( tags );
				var c = Math.Min( PhysicsTrace.Request.NumTagFields, first.Length );

				for ( var i = 0; i < c; i++ ) t.request.TagAny[i] = first[i];

				return t;
			}

			foreach ( var te in ITagSet.Copy( tags ) )
			{
				for ( int i = 0; i < PhysicsTrace.Request.NumTagFields; i++ )
				{
					if ( t.request.TagAny[i] == te ) return this;

					if ( t.request.TagAny[i] == 0 )
					{
						t.request.TagAny[i] = te;
					}
				}
			}

			return t;
		}

		/// <summary>Only return with any of these tags</summary>
		public unsafe PhysicsTraceBuilder WithAnyTags( ITagSet tags )
		{
			if ( tags is null ) return this;

			var t = this;

			foreach ( var token in ITagSet.Copy( tags.GetTokens() ) )
			{
				t = t.WithOptionalTag( new StringToken( token ) );
			}

			return t;
		}

		/// <summary>Only return without this tag</summary>
		public unsafe PhysicsTraceBuilder WithoutTag( StringToken tag )
		{
			for ( int i = 0; i < PhysicsTrace.Request.NumTagFields; i++ )
			{
				if ( request.TagExclude[i] == tag.Value ) return this;

				if ( request.TagExclude[i] == 0 )
				{
					request.TagExclude[i] = tag.Value;
					return this;
				}
			}

			return this;
		}

		/// <summary>Only return without any of these tags</summary>
		public unsafe PhysicsTraceBuilder WithoutTags( params string[] tags )
		{
			var t = this;

			foreach ( var tag in tags )
			{
				t = t.WithoutTag( tag );
			}

			return t;
		}

		/// <summary>Only return without any of these tags</summary>
		public unsafe PhysicsTraceBuilder WithoutTags( ITagSet tags )
		{
			if ( tags is null ) return this;

			var t = this;

			foreach ( var token in ITagSet.Copy( tags.GetTokens() ) )
			{
				t = t.WithoutTag( new StringToken( token ) );
			}

			return t;
		}

		/// <summary>Use the collision rules of the given tag.</summary>
		public readonly PhysicsTraceBuilder WithCollisionRules( string tag, bool asTrigger = false )
		{
			var rules = targetWorld?.Backend?.CompiledCollisionRules;
			var mask = rules is null || string.IsNullOrEmpty( tag ) ? 0ul : rules.BitFor( ( (StringToken)tag ).Value );

			return WithRules( mask, asTrigger );
		}

		/// <summary>Use the collision rules for the given set of tags.</summary>
		public readonly PhysicsTraceBuilder WithCollisionRules( IEnumerable<string> tags, bool asTrigger = false )
		{
			var rules = targetWorld?.Backend?.CompiledCollisionRules;
			var mask = 0ul;

			//A tag the rules do not name has no bit, as on a body - and a body with no bit at all collides with everything.
			if ( rules is not null && tags is not null )
			{
				foreach ( var tag in tags )
				{
					if ( !string.IsNullOrEmpty( tag ) ) mask |= rules.BitFor( ( (StringToken)tag ).Value );
				}
			}

			return WithRules( mask, asTrigger );
		}

		//Takes the rules of a body whose tags are these bits of the world's compiled rules.
		private readonly PhysicsTraceBuilder WithRules( ulong mask, bool asTrigger )
		{
			var t = this;

			t.request.RulesMask = mask;
			t.request.RulesLimit = asTrigger ? CoreCollisionRules.Trigger : CoreCollisionRules.Collide;

			return asTrigger ? t.HitTriggers() : t;
		}
	}
}
