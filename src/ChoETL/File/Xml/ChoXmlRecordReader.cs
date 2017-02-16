﻿using GotDotNet.XPath;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace ChoETL
{
    internal class ChoXmlRecordReader : ChoRecordReader
    {
        private IChoNotifyRecordRead _callbackRecord;
        private bool _configCheckDone = false;

        public ChoXmlRecordConfiguration Configuration
        {
            get;
            private set;
        }

        public ChoXmlRecordReader(Type recordType, ChoXmlRecordConfiguration configuration) : base(recordType)
        {
            ChoGuard.ArgumentNotNull(configuration, "Configuration");
            Configuration = configuration;

            _callbackRecord = ChoMetadataObjectCache.CreateMetadataObject<IChoNotifyRecordRead>(recordType);

            //Configuration.Validate();
        }

        public override void LoadSchema(object source)
        {
            var e = AsEnumerable(source, ChoETLFramework.TraceSwitchOff).GetEnumerator();
            e.MoveNext();
        }

        public override IEnumerable<object> AsEnumerable(object source, Func<object, bool?> filterFunc = null)
        {
            return AsEnumerable(source, TraceSwitch, filterFunc);
        }

        private IEnumerable<object> AsEnumerable(object source, TraceSwitch traceSwitch, Func<object, bool?> filterFunc = null)
        {
            TraceSwitch = traceSwitch;

            XmlReader sr = source as XmlReader;
            ChoGuard.ArgumentNotNull(sr, "XmlReader");

            //sr.Seek(0, SeekOrigin.Begin);

            if (!RaiseBeginLoad(sr))
                yield break;

            XPathCollection xc = Configuration.NamespaceManager == null ? new XPathCollection() : new XPathCollection(Configuration.NamespaceManager);
            int childQuery = xc.Add(Configuration.XPath);
            XPathReader xpr = new XPathReader(sr, xc);
            int counter = 0;
            Tuple<int, XElement> pair = null;

            while (xpr.ReadUntilMatch())
            {
                if (xpr.Match(childQuery))
                {
                    XElement el = XDocument.Load(xpr.ReadSubtree()).Root as XElement;

                    pair = new Tuple<int, XElement>(++counter, el);

                    if (!_configCheckDone)
                    {
                        Configuration.Validate(pair);
                        _configCheckDone = true;
                    }

                    object rec = ChoActivator.CreateInstance(RecordType);
                    ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, "Loading node [{0}]...".FormatString(pair.Item1));
                    if (!LoadNode(pair, ref rec))
                        yield break;
                    
                    if (rec == null)
                        continue;

                    yield return rec;
                }
            }

            RaiseEndLoad(sr);
        }

        private bool LoadNode(Tuple<int, XElement> pair, ref object rec)
        {
            try
            {
                if (!RaiseBeforeRecordLoad(rec, ref pair))
                    return false;

                if (pair.Item2 == null)
                {
                    rec = null;
                    return true;
                }

                if (!FillRecord(rec, pair))
                    return false;

                rec.DoObjectLevelValidation(Configuration, Configuration.XmlRecordFieldConfigurations.ToArray());

                if (!RaiseAfterRecordLoad(rec, pair))
                    return false;
            }
            catch (ChoParserException)
            {
                throw;
            }
            catch (ChoMissingRecordFieldException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ChoETLFramework.HandleException(ex);
                if (Configuration.ErrorMode == ChoErrorMode.IgnoreAndContinue)
                {
                    rec = null;
                }
                else if (Configuration.ErrorMode == ChoErrorMode.ReportAndContinue)
                {
                    if (!RaiseRecordLoadError(rec, pair, ex))
                        throw;
                }
                else
                    throw;

                return true;
            }

            return true;
        }

        private bool FillRecord(object rec, Tuple<int, XElement> pair)
        {
            int lineNo;
            XElement node;

            lineNo = pair.Item1;
            node = pair.Item2;

            //string[] fieldValues = (from x in node.Split(Configuration.XPath, Configuration.StringSplitOptions, Configuration.QuoteChar)
            //                        select x).ToArray();
            //if (Configuration.ColumnCountStrict)
            //{
            //    if (fieldValues.Length != Configuration.XmlRecordFieldConfigurations.Count)
            //        throw new ChoParserException("Incorrect number of field values found at line [{2}]. Expected [{0}] field values. Found [{1}] field values.".FormatString(Configuration.XmlRecordFieldConfigurations.Count, fieldValues.Length, pair.Item1));
            //}

            //Dictionary<string, string> fieldNameValues = ToFieldNameValues(fieldValues);

            //ValidateLine(pair.Item1, fieldValues);

            object fieldValue = null;
            ChoXmlRecordFieldConfiguration fieldConfig = null;
            foreach (KeyValuePair<string, ChoXmlRecordFieldConfiguration> kvp in Configuration.RecordFieldConfigurationsDict)
            {
                fieldValue = null;
                fieldConfig = kvp.Value;

                XElement fXElement = ((IEnumerable)node.XPathEvaluate(fieldConfig.XPath)).OfType<XElement>().FirstOrDefault();
                if (fXElement != null)
                    fieldValue = fXElement.Value;
                else
                {
                    XAttribute fXAttribute = ((IEnumerable)node.XPathEvaluate(fieldConfig.XPath)).OfType<XAttribute>().FirstOrDefault();
                    if (fXAttribute != null)
                        fieldValue = fXAttribute.Value;
                    else if (Configuration.ColumnCountStrict)
                        throw new ChoParserException("Missing '{0}' xml node.".FormatString(fieldConfig.FieldName));
                }

                if (rec is ExpandoObject)
                {
                    if (kvp.Value.FieldType == null)
                        kvp.Value.FieldType = typeof(string);
                }
                else
                {
                    if (ChoType.HasProperty(rec.GetType(), kvp.Key))
                    {
                        kvp.Value.FieldType = ChoType.GetMemberType(rec.GetType(), kvp.Key);
                    }
                    else
                        kvp.Value.FieldType = typeof(string);
                }

                fieldValue = CleanFieldValue(fieldConfig, kvp.Value.FieldType, fieldValue as string);

                if (!RaiseBeforeRecordFieldLoad(rec, pair.Item1, kvp.Key, ref fieldValue))
                    return false;

                try
                {
                    bool ignoreFieldValue = fieldConfig.IgnoreFieldValue(fieldValue);
                    if (ignoreFieldValue)
                        fieldValue = null;

                    if (rec is ExpandoObject)
                    {
                        var dict = rec as IDictionary<string, Object>;

                        dict.SetDefaultValue(kvp.Key, kvp.Value, Configuration.Culture);

                        if (ignoreFieldValue)
                            dict.AddOrUpdate(kvp.Key, fieldValue);
                        else
                            dict.ConvertNSetMemberValue(kvp.Key, kvp.Value, ref fieldValue, Configuration.Culture);

                        dict.DoMemberLevelValidation(kvp.Key, kvp.Value, Configuration.ObjectValidationMode);
                    }
                    else
                    {
                        if (ChoType.HasProperty(rec.GetType(), kvp.Key))
                        {
                            rec.SetDefaultValue(kvp.Key, kvp.Value, Configuration.Culture);

                            if (!ignoreFieldValue)
                                rec.ConvertNSetMemberValue(kvp.Key, kvp.Value, ref fieldValue, Configuration.Culture);
                        }
                        else
                            throw new ChoMissingRecordFieldException("Missing '{0}' property in {1} type.".FormatString(kvp.Key, ChoType.GetTypeName(rec)));

                        rec.DoMemberLevelValidation(kvp.Key, kvp.Value, Configuration.ObjectValidationMode);
                    }

                    if (!RaiseAfterRecordFieldLoad(rec, pair.Item1, kvp.Key, fieldValue))
                        return false;
                }
                catch (ChoParserException)
                {
                    throw;
                }
                catch (ChoMissingRecordFieldException)
                {
                    if (Configuration.ThrowAndStopOnMissingField)
                        throw;
                }
                catch (Exception ex)
                {
                    ChoETLFramework.HandleException(ex);

                    if (fieldConfig.ErrorMode == ChoErrorMode.ThrowAndStop)
                        throw;

                    try
                    {
                        if (rec is ExpandoObject)
                        {
                            var dict = rec as IDictionary<string, Object>;

                            if (dict.SetFallbackValue(kvp.Key, kvp.Value, Configuration.Culture, ref fieldValue))
                            {
                                dict.DoMemberLevelValidation(kvp.Key, kvp.Value, Configuration.ObjectValidationMode);
                            }
                            else
                                throw new ChoParserException($"Failed to parse '{fieldValue}' value for '{fieldConfig.FieldName}' field.", ex);
                        }
                        else if (ChoType.HasProperty(rec.GetType(), kvp.Key) && rec.SetFallbackValue(kvp.Key, kvp.Value, Configuration.Culture))
                        {
                            rec.DoMemberLevelValidation(kvp.Key, kvp.Value, Configuration.ObjectValidationMode);
                        }
                        else
                            throw new ChoParserException($"Failed to parse '{fieldValue}' value for '{fieldConfig.FieldName}' field.", ex);
                    }
                    catch (Exception innerEx)
                    {
                        if (ex == innerEx.InnerException)
                        {
                            if (fieldConfig.ErrorMode == ChoErrorMode.IgnoreAndContinue)
                            {
                                continue;
                            }
                            else
                            {
                                if (!RaiseRecordFieldLoadError(rec, pair.Item1, kvp.Key, fieldValue, ex))
                                    throw new ChoParserException($"Failed to parse '{fieldValue}' value for '{fieldConfig.FieldName}' field.", ex);
                            }
                        }
                        else
                        {
                            throw new ChoParserException("Failed to assign '{0}' fallback value to '{1}' field.".FormatString(fieldValue, fieldConfig.FieldName), innerEx);
                        }
                    }
                }
            }

            return true;
        }

        private string CleanFieldValue(ChoXmlRecordFieldConfiguration config, Type fieldType, string fieldValue)
        {
            if (fieldValue.IsNull()) return fieldValue;

            if (fieldValue != null)
            {
                ChoFieldValueTrimOption fieldValueTrimOption = ChoFieldValueTrimOption.Trim;

                if (config.FieldValueTrimOption == null)
                {
                    //if (fieldType == typeof(string))
                    //    fieldValueTrimOption = ChoFieldValueTrimOption.None;
                }
                else
                    fieldValueTrimOption = config.FieldValueTrimOption.Value;

                switch (fieldValueTrimOption)
                {
                    case ChoFieldValueTrimOption.Trim:
                        fieldValue = fieldValue.Trim();
                        break;
                    case ChoFieldValueTrimOption.TrimStart:
                        fieldValue = fieldValue.TrimStart();
                        break;
                    case ChoFieldValueTrimOption.TrimEnd:
                        fieldValue = fieldValue.TrimEnd();
                        break;
                }
            }

            if (config.Size != null)
            {
                if (fieldValue.Length > config.Size.Value)
                {
                    if (!config.Truncate)
                        throw new ChoParserException("Incorrect field value length found for '{0}' member [Expected: {1}, Actual: {2}].".FormatString(config.FieldName, config.Size.Value, fieldValue.Length));
                    else
                        fieldValue = fieldValue.Substring(0, config.Size.Value);
                }
            }

            return System.Net.WebUtility.HtmlDecode(fieldValue);
        }

        private bool RaiseBeginLoad(object state)
        {
            if (_callbackRecord == null) return true;
            return ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.BeginLoad(state), true);
        }

        private void RaiseEndLoad(object state)
        {
            if (_callbackRecord == null) return;
            ChoActionEx.RunWithIgnoreError(() => _callbackRecord.EndLoad(state));
        }

        private bool RaiseBeforeRecordLoad(object target, ref Tuple<int, XElement> pair)
        {
            if (_callbackRecord == null) return true;
            int index = pair.Item1;
            object state = pair.Item2;
            bool retValue = ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.BeforeRecordLoad(target, index, ref state), true);

            if (retValue)
                pair = new Tuple<int, XElement>(index, state as XElement);

            return retValue;
        }

        private bool RaiseAfterRecordLoad(object target, Tuple<int, XElement> pair)
        {
            if (_callbackRecord == null) return true;
            return ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.AfterRecordLoad(target, pair.Item1, pair.Item2), true);
        }

        private bool RaiseRecordLoadError(object target, Tuple<int, XElement> pair, Exception ex)
        {
            if (_callbackRecord == null) return true;
            return ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.RecordLoadError(target, pair.Item1, pair.Item2, ex), false);
        }

        private bool RaiseBeforeRecordFieldLoad(object target, int index, string propName, ref object value)
        {
            if (_callbackRecord == null) return true;
            object state = value;
            bool retValue = ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.BeforeRecordFieldLoad(target, index, propName, ref state), true);

            if (retValue)
                value = state;

            return retValue;
        }

        private bool RaiseAfterRecordFieldLoad(object target, int index, string propName, object value)
        {
            if (_callbackRecord == null) return true;
            return ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.AfterRecordFieldLoad(target, index, propName, value), true);
        }

        private bool RaiseRecordFieldLoadError(object target, int index, string propName, object value, Exception ex)
        {
            if (_callbackRecord == null) return false;
            return ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.RecordFieldLoadError(target, index, propName, value, ex), false);
        }
    }
}