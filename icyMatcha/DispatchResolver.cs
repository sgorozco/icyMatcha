using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace icyMatcha {
   public class DispatchResolver<TDelegate> where TDelegate: Delegate {
      
      private const string s_errMethodNotFound = "A Method '{0}' accepting parameters of type(s) [{1}] was not found on type {2}";
      private readonly bool _multithreadAware;
      private readonly Dictionary<long, Delegate> _delegateCache = new Dictionary<long, Delegate>();

      public string MethodName { get; private set; }

      public DispatchResolver(string methodName, bool multithreadAware = false) {
         this.MethodName = methodName;
         _multithreadAware = multithreadAware;
      }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      private static void CombineHash(ref long seed, int hash) {
         // taken from boost::hash_combine(), the magic number has been extended to 64 bits
         // http://stackoverflow.com/questions/4948780/magic-number-in-boosthash-combine
         seed ^= hash + unchecked((long)0x9e3779b97f4a7c15) + (seed << 6) + (seed >> 2);
      }

      private static long ComputeSignature(Type targetType, Type[] parameterTypes, string routingKey) {

         // The signature is fromed by mixing Type.GetHashCode() operations. For types, it can be guaranteed that the
         // hashcode will be unique (collisions will start ocurring only if more than 2^32 types are used in a program)
         // We will use the same algorithm as boost::hash_combine assuming it leads to an infinitesimal collision
         // probability

         long seed = targetType.GetHashCode();
         for( int i = 0; i < parameterTypes.Length; i++ ) {
            CombineHash(ref seed, parameterTypes[i].GetHashCode());
         }
         if( routingKey != null ) {
            CombineHash(ref seed, routingKey.GetHashCode());
         }
         return seed;
      }

      // From http://stackoverflow.com/questions/4185521/c-sharp-get-generic-type-name - Ali's answer
      private static string GetFriendlyTypeName(Type type) {
         var friendlyName = type.Name;
         if( !type.IsGenericType ) return friendlyName;

         var iBacktick = friendlyName.IndexOf('`');
         if( iBacktick > 0 ) friendlyName = friendlyName.Remove(iBacktick);

         var genericParameters = type.GetGenericArguments().Select(x => GetFriendlyTypeName(x));
         friendlyName += "<" + string.Join(", ", genericParameters) + ">";
         return friendlyName;
      }

      private static string ExpectedDelegateSignature(int parameterCount, bool isFunc) {

         // The created delegate is an open delegate (not bound to a specific instance), open delegates require to
         // explicitly pass the "this" parameter of the instance as the first parameter of the delegate. So we will
         // add one to the actual number of parameters of the method that will be delegated

         parameterCount++;
         var sb = new StringBuilder();
         if( !isFunc ) {
            sb.Append("Action<");
         } else {
            sb.Append("Func<");
            // Func delegates append an extra type parameter for the return type
            parameterCount++;
         }

         // Our weakly-typed wrapper will consist of merely Object-type instances for all parameter types
         for( int i = 0; i < parameterCount; i++ ) {
            sb.Append("object, ");
         }

         // remove trailing comma
         sb.Length -= 2;
         sb.Append('>');
         return sb.ToString();
      }

      private static Exception IncompatibleSignature(int expectedParameterCount, bool isFunc) {
         string msg = "TDelegate is incompatible with methodToWrap. "
                    + "Expected TDelegate signature is: "
                    + ExpectedDelegateSignature(expectedParameterCount, isFunc).ToString();
         return new InvalidOperationException(msg);
      }

      private static TDelegate WrapMethod(Type callingType, MethodInfo methodToWrap, bool isFunc) {
         
         // reflect on the supplied TDelegate type parameter
         var actionInvokeMethod = typeof(TDelegate).GetMethod("Invoke");
         var actionParameters = actionInvokeMethod.GetParameters();
         
         // reflect on the method that will be wrapped by our delegate
         var expectedTypedParameters = methodToWrap.GetParameters();

         // validate that the number of type parameters in TDelegate is compatible with the method being wrapped
         // we will subtract 1 from the type parameter count because TDelegate is an open delegate that carries an
         // extra parameter as the 'this' placeholder
         if( actionParameters.Length - 1 != expectedTypedParameters.Length ) {
            throw IncompatibleSignature(expectedTypedParameters.Length, isFunc);
         }


         // Use linq Expression API to compile a delegate that would look something like this:
         /*
         (object instance, params object[] arguments) => {
             // Typecast parameters to perform a strongly-typed call to the adequate wrapped method
             ((CallingType)instance).Method((TParam0)arguments[0], (TParam1)arguments[1], ...);
          };  
         */

         var untypedInstance = Expression.Parameter(typeof(object), "instance");
         var untypedParameters = new ParameterExpression[expectedTypedParameters.Length + 1]; // account for 'this'
         var typeCastedParameters = new UnaryExpression[expectedTypedParameters.Length];
         untypedParameters[0] = untypedInstance;  // set 'this'

         for( int i = 0; i < expectedTypedParameters.Length; i++ ) {

            // prepare the untyped (object) parameters that will receive the compiled delegate
            untypedParameters[i + 1] = Expression.Parameter(typeof(object), "param" + i.ToString());

            // prepare the strongly typed parameters that will be actually received by the wrapped method
            // by adding a Convert (cast) operator from object to the strong type expected by the method
            typeCastedParameters[i] = Expression.Convert(untypedParameters[i + 1],
                                                         expectedTypedParameters[i].ParameterType);
         }
         var castedInstance = Expression.Convert(untypedInstance, callingType);
         var methodCall = Expression.Call(castedInstance,
                                          methodToWrap,
                                          typeCastedParameters);

         var weakTypedDelegate = Expression.Lambda<TDelegate>(methodCall, untypedParameters).Compile();
         return weakTypedDelegate;
      }

      private static bool TryReflectionLookupMethod(
        Type callingClassType,
        string methodName,
        out MethodInfo methodInfo,
        params Type[] parameterTypes
     ) {
         BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

         // Reflection uses a very exhaustive search, for the method that best matches the parameter types, taking
         // into account inheritance (it will select the most specific method that matches the parameter types)
         methodInfo = callingClassType.GetMethod(methodName, flags, null, parameterTypes, null);
         return methodInfo != null;
      }

      
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      private bool CachedLookup(long signature, out Delegate result) {
         if( _delegateCache.TryGetValue(signature, out result) ) {
            return true;
         }
         return false;
      }

      private bool TryGetDelegate(
         Type targetType,
         bool isFunc,
         string routingKey,
         out TDelegate outDelegate,
         params Type[] parameterTypes
      ) {
         Delegate result;
         long signature = ComputeSignature(targetType, parameterTypes, routingKey);

         //-----------------------------------------------------------
         // Fast Path
         if( !_multithreadAware ) {
            if( CachedLookup(signature, out result) ) {
               outDelegate = result as TDelegate;
               return true;
            }
         } else {
            lock( _delegateCache ) {
               if( CachedLookup(signature, out result) ) {
                  outDelegate = result as TDelegate;
                  return true;
               }
            }
         }

         //-----------------------------------------------------------------------------------------------------
         // Slow path
         // The desired method has not been cached previously, we need to lookup the method and assemble a
         // delegate expression that will be able to invoke the method without relying on reflection

         string methodName = (routingKey == null) ? MethodName : routingKey + MethodName;
         if( !TryReflectionLookupMethod(targetType, methodName, out MethodInfo matchingMethod, parameterTypes) ) {
            // Method not found!
            outDelegate = null;
            return false;
         }
         
         result = WrapMethod(targetType, matchingMethod, isFunc) as Delegate;
         if( !_multithreadAware ) {
            _delegateCache[signature] = result;
         } else {
            lock( _delegateCache ) {
               _delegateCache[signature] = result;
            }
         }
         outDelegate = result as TDelegate;
         return true;
      }

      private TDelegate GetDelegate(Type targetType, bool isFunc, string routingKey, params Type[] parameterTypes) {
         if( TryGetDelegate(targetType, isFunc, routingKey, out TDelegate result, parameterTypes) ) {
            return result;
         }
         string @params = string.Join(", ", parameterTypes.Select(t => GetFriendlyTypeName(t)));
         throw new MissingMethodException(string.Format(s_errMethodNotFound, this.MethodName, @params));
      }

      //----------------------------------------------- Public API -----------------------------------------------------

      public TDelegate GetDelegate(Type targetType, params Type[] parameterTypes) {
         return GetDelegate(targetType, false, null, parameterTypes);
      }

      public TDelegate GetDelegate(Type targetType, string routingKey, params Type[] parameterTypes) {
         return GetDelegate(targetType, false, routingKey, parameterTypes);
      }

      public bool TryGetDelegate(Type targetType, out TDelegate result, params Type[] parameterTypes) {
         return TryGetDelegate(targetType, false, null, out result, parameterTypes);
      }

      public bool TryGetDelegate(Type targetType, string routingKey, out TDelegate result, params Type[] parameterTypes) {
         return TryGetDelegate(targetType, false, routingKey, out result, parameterTypes);
      }

   }
}
