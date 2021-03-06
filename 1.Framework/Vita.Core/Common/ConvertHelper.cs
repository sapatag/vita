﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Vita.Common {
  public static class ConvertHelper {
    public static T FromString<T>(string value) {
      var type = typeof(T);
      if(type == typeof(string))
        return (T)(object)value;
      return (T)ChangeType(value, type);
    }

    public static string ValueToString(object value) {
      if(value == null)
        return string.Empty;
      var type = value.GetType();
      if(type == typeof(string))
        return (string)value;
      if(type == typeof(DateTime))
        return DateTimeToUniString((DateTime)value);
      return value.ToString();
    }

    public static string DateTimeToUniString(DateTime value) {
      var uniFmt = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.fffff";
      var result = value.ToString(uniFmt); 
      return result;
    }


    public static object ChangeType(object value, Type type) {
      if(value == null)
        return null;
      var valueType = value.GetType();
      if(valueType == type)
        return value; //it might happen for Nullable<T> -> T 
      //specific cases
      if(type == typeof(string)) {
        if(value is string)
          return (string)value;
        return value.ToString();
      }
      if(valueType == typeof(string))
        return FromString((string)value, type);
      //else try default Convert
      return Convert.ChangeType(value, type);
    }

    public static object FromString(string value, Type type) {
      if(string.IsNullOrEmpty(value)) {
        if(!type.IsValueType || type.IsNullableValueType())
          return null;
        Util.Throw("Failed to convert empty string to type {0}", type);
        return null;
      }
      if(type.IsNullableValueType()) {
        var vType = Nullable.GetUnderlyingType(type);
        var v = FromString(value, vType);
        return MakeNullable(v, vType);
      }
      if(type == typeof(Guid)) {
        Guid g;
        Util.Check(Guid.TryParse(value, out g), "Invalid Guid format: {0}", value);
        return g;
      }
      if(type.IsEnum)
        return Enum.Parse(type, value, ignoreCase: true);
      if(type.IsInt()) {
        int i;
        Util.Check(int.TryParse(value, out i), "Invalid integer: {0}", value);
        return Convert.ChangeType(i, type);
      }
      if(type == typeof(Single) || type == typeof(Double)) {
        double d;
        Util.Check(double.TryParse(value, out d), "Invalid float: {0}", value);
        return Convert.ChangeType(d, type);
      }
      //else try default Convert
      return Convert.ChangeType(value, type);
    }

    public static TEnum[] ParseEnumArray<TEnum>(string value, char separator = ',') where TEnum : struct {
      if(string.IsNullOrWhiteSpace(value))
        return null;
      var type = typeof(TEnum);
      Util.Check(type.IsEnum, "Type {0} is not enum.", type);
      var strArr = value.SplitNames(separator);
      var arr = new TEnum[strArr.Length];
      for(int i = 0; i < arr.Length; i++) {
        TEnum e;
        if(Enum.TryParse<TEnum>(strArr[i], out e))
          arr[i] = e;
      }
      return arr;
    }

    public static DateTime? TryParseDateTime(string value) {
      if(string.IsNullOrWhiteSpace(value))
        return null;
      DateTime dt;
      if(DateTime.TryParse(value, out dt))
        return dt;
      return null;
    }

    public static List<string> FlagsToStringList<TEnum>(TEnum value) where TEnum : struct {
      Util.Check(typeof(TEnum).IsEnum, "Expected enum type");
      return value.ToString().SplitNames(',').ToList();
    }

    public static IEnumerable ConvertEnumerable(IEnumerable source, Type resultType) {
      if(source.GetType() == resultType)
        return source;
      Util.Check(resultType.IsGenericType, "Invalid result type: {0}. Expected generic IEnumerable<T> type.", resultType);
      var resultElemType = resultType.GetGenericArguments()[0];
      var sourceElemType = source.GetType().GetGenericArguments()[0];
      // Very special case: IEnumerable<T> -> IEnumerable<T?>
      if(resultElemType.IsNullableOf(sourceElemType)) {
        _convertToEnumerableOfNullablesMethod = _convertToEnumerableOfNullablesMethod ??
            typeof(ConvertHelper).GetMethod("ConvertToEnumerableOfNullables", BindingFlags.Static | BindingFlags.NonPublic);
        var genMethod = _convertToEnumerableOfNullablesMethod.MakeGenericMethod(sourceElemType);
        var result = genMethod.Invoke(null, new[] { source });
        return result as IEnumerable;
      }
      //General case
      var listType = typeof(List<>).MakeGenericType(resultElemType);
      var list = Activator.CreateInstance(listType) as IList;
      foreach(var obj in source) {
        var value = obj;
        if(obj.GetType() != resultElemType)
          value = ConvertHelper.ChangeType(obj, resultElemType);
        list.Add(value);
      }
      return list;
    }

    private static IEnumerable<T?> ConvertToEnumerableOfNullables<T>(IEnumerable<T> source) where T : struct {
      return source.Select(v => new Nullable<T>(v)).ToList();
    }

    public static object MakeNullable(object value, Type valueType) {
      _makeNullableMethod = _makeNullableMethod ?? typeof(ConvertHelper).GetMethod("MakeNullable", BindingFlags.Static | BindingFlags.NonPublic);
      var genMethod = _makeNullableMethod.MakeGenericMethod(valueType);
      var result = genMethod.Invoke(null, new[] { value });
      return result; 
    }

    internal static Nullable<T> MakeNullable<T>(T value) where T : struct {
      return new Nullable<T>(value); 

    }

    static MethodInfo _makeNullableMethod;
    static MethodInfo _convertToEnumerableOfNullablesMethod;
  }
}