using Facepunch.ActionGraphs;

namespace Rinten;

public static partial class RintenSystemExtensions
{
	private static partial IEnumerable<Type> EitherOptions( Type type ) => Either.IsEitherType( type ) ? Either.Unwrap( type ) : null;
}
