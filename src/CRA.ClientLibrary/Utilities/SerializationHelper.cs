﻿using System;
using System.Reflection;
using System.Linq.Expressions;
using Newtonsoft.Json;
using Remote.Linq;
using Remote.Linq.ExpressionVisitors;
using Aq.ExpressionJsonSerializer;

namespace CRA.ClientLibrary
{
    internal class SerializationHelper
    {
        private SerializationHelper() { }

        private static readonly JsonSerializerSettings _serializerSettings
            = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                NullValueHandling = NullValueHandling.Ignore,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Full,
            };

        private static readonly ExpressionJsonConverter _expressionJsonConverter
            = new ExpressionJsonConverter(Assembly.GetAssembly(typeof(SerializationHelper)));

        /// <summary>
        /// Serializes a LINQ expression.
        /// </summary>
        /// <param name="expression">The expression.</param>
        /// <returns>The serialized expression.</returns>
        internal static string Serialize(Expression expression)
        {
            if (!_serializerSettings.Converters.Contains(_expressionJsonConverter))
                _serializerSettings.Converters.Add(_expressionJsonConverter);

            var toSerialize = expression.ToRemoteLinqExpression()
                                        .ReplaceGenericQueryArgumentsByNonGenericArguments();
            return JsonConvert.SerializeObject(toSerialize, _serializerSettings);
        }

        /// <summary>
        /// Deserializes a LINQ expression.
        /// </summary>
        /// <param name="expression">The serialized expression.</param>
        /// <returns>The expression.</returns>
        internal static Expression Deserialize(string expression)
        {
            if (!_serializerSettings.Converters.Contains(_expressionJsonConverter))
                _serializerSettings.Converters.Add(_expressionJsonConverter);

            var deserialized = JsonConvert.DeserializeObject<Remote.Linq.Expressions.LambdaExpression>(
                                                                expression, _serializerSettings);
            var ret = deserialized.ReplaceNonGenericQueryArgumentsByGenericArguments().
                                        ToTypeCastedRemoteExpression().ToLinqExpression();
            return ret;
        }

        internal static string SerializeObject(object obj)
        {
            if (obj == null)
            {
                return JsonConvert.SerializeObject(obj, _serializerSettings);
            }
            var tmp = new ObjectWrapper
            {
                type = obj.GetType().AssemblyQualifiedName,
                data = JsonConvert.SerializeObject(obj, _serializerSettings)
            };

            return JsonConvert.SerializeObject(tmp, typeof(ObjectWrapper), _serializerSettings);
        }

        internal static object DeserializeObject(string obj)
        {
            if (obj == "null") return null;
            var ow = JsonConvert.DeserializeObject<ObjectWrapper>(obj, _serializerSettings);
            return JsonConvert.DeserializeObject(ow.data, Type.GetType(ow.type), _serializerSettings);
        }
    }
}
