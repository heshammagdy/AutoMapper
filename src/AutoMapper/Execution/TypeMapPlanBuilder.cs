﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace AutoMapper.Execution
{
    using Configuration;
    using Mappers;
    using static Expression;
    using static ExpressionExtensions;

    public class TypeMapPlanBuilder
    {
        private static readonly Expression<Func<IRuntimeMapper, ResolutionContext>> CreateContext = mapper => new ResolutionContext(mapper.DefaultContext.Options, mapper);
        private static readonly Expression<Func<AutoMapperMappingException>> CtorExpression = () => new AutoMapperMappingException(null, null, default(TypePair), null, null);
        private static readonly Expression<Action<ResolutionContext>> IncTypeDepthInfo = ctxt => ctxt.IncrementTypeDepth(default(TypePair));
        private static readonly Expression<Action<ResolutionContext>> DecTypeDepthInfo = ctxt => ctxt.DecrementTypeDepth(default(TypePair));
        private static readonly Expression<Func<ResolutionContext, int>> GetTypeDepthInfo = ctxt => ctxt.GetTypeDepth(default(TypePair));

        private readonly IConfigurationProvider _configurationProvider;
        private readonly TypeMap _typeMap;
        private readonly TypeMapRegistry _typeMapRegistry;
        private readonly ParameterExpression _source;
        private readonly ParameterExpression _initialDestination;
        private readonly ParameterExpression _context;
        private readonly ParameterExpression _destination;

        public TypeMapPlanBuilder(IConfigurationProvider configurationProvider, TypeMapRegistry typeMapRegistry, TypeMap typeMap)
        {
            _configurationProvider = configurationProvider;
            _typeMapRegistry = typeMapRegistry;
            _typeMap = typeMap;
            _source = Parameter(typeMap.SourceType, "src");
            _initialDestination = Parameter(typeMap.DestinationTypeToUse, "dest");
            _context = Parameter(typeof(ResolutionContext), "ctxt");
            _destination = Variable(_initialDestination.Type, "typeMapDestination");
        }

        public LambdaExpression CreateMapperLambda()
        {
            if(_typeMap.SourceType.IsGenericTypeDefinition() || _typeMap.DestinationTypeToUse.IsGenericTypeDefinition())
            {
                return null;
            }
            var customExpression = TypeConverterMapper() ?? _typeMap.Substitution ?? _typeMap.CustomMapper ?? _typeMap.CustomProjection;
            if(customExpression != null)
            {
                return Lambda(customExpression.ReplaceParameters(_source, _initialDestination, _context), _source, _initialDestination, _context);
            }
            bool constructorMapping;

            var destinationFunc = CreateDestinationFunc(out constructorMapping);

            var assignmentFunc = CreateAssignmentFunc(destinationFunc, constructorMapping);

            var mapperFunc = CreateMapperFunc(assignmentFunc);

            var checkContext = CheckContext(_typeMap, _context);
            var lambaBody = (checkContext != null) ? new[] { checkContext, mapperFunc } : new[] { mapperFunc };

            return Lambda(Block(new[] { _destination }, lambaBody), _source, _initialDestination, _context);
        }

        private LambdaExpression TypeConverterMapper()
        {
            if(_typeMap.TypeConverterType == null)
            {
                return null;
            }
            Type type;
            if(_typeMap.TypeConverterType.IsGenericTypeDefinition())
            {
                var genericTypeParam = _typeMap.SourceType.IsGenericType()
                    ? _typeMap.SourceType.GetTypeInfo().GenericTypeArguments[0]
                    : _typeMap.DestinationTypeToUse.GetTypeInfo().GenericTypeArguments[0];
                type = _typeMap.TypeConverterType.MakeGenericType(genericTypeParam);
            }
            else
            {
                type = _typeMap.TypeConverterType;
            }
            // (src, dest, ctxt) => ((ITypeConverter<TSource, TDest>)ctxt.Options.CreateInstance<TypeConverterType>()).ToType(src, ctxt);
            var converterInterfaceType = typeof(ITypeConverter<,>).MakeGenericType(_typeMap.SourceType, _typeMap.DestinationTypeToUse);
            return Lambda(
                Call(
                    ToType(
                        Call(
                            MakeMemberAccess(_context, typeof(ResolutionContext).GetDeclaredProperty("Options")),
                            typeof(IMappingOperationOptions).GetDeclaredMethod("CreateInstance")
                                .MakeGenericMethod(type)
                            ),
                        converterInterfaceType),
                    converterInterfaceType.GetDeclaredMethod("Convert"),
                    _source, _initialDestination, _context
                    ),
                _source, _initialDestination, _context);
        }

        public static ConditionalExpression CheckContext(TypeMap typeMap, Expression context)
        {
            if(typeMap.MaxDepth > 0 || typeMap.PreserveReferences)
            {
                var mapper = Property(context, "Mapper");
                return IfThen(Property(context, "IsDefault"), Assign(context, Invoke(CreateContext, mapper)));
            }
            return null;
        }

        private Expression CreateDestinationFunc(out bool constructorMapping)
        {
            var newDestFunc = ToType(CreateNewDestinationFunc(out constructorMapping), _typeMap.DestinationTypeToUse);

            var getDest = _typeMap.DestinationTypeToUse.GetTypeInfo().IsValueType
                ? newDestFunc
                : Coalesce(_initialDestination, newDestFunc);

            Expression destinationFunc = Assign(_destination, getDest);

            if(_typeMap.PreserveReferences)
            {
                var dest = Variable(typeof(object), "dest");

                Expression valueBag = Property(_context, "InstanceCache");
                var set = Assign(Property(valueBag, "Item", _source), dest);
                var setCache =
                    IfThen(NotEqual(_source, Constant(null)), set);

                destinationFunc = Block(new[] { dest }, Assign(dest, destinationFunc), setCache, dest);
            }
            return destinationFunc;
        }

        private Expression CreateAssignmentFunc(Expression destinationFunc, bool constructorMapping)
        {
            var actions = new List<Expression>();
            foreach(var propertyMap in _typeMap.GetPropertyMaps())
            {
                if(!propertyMap.CanResolveValue())
                {
                    continue;
                }
                var property = TryPropertyMap(propertyMap);
                if(constructorMapping && _typeMap.ConstructorParameterMatches(propertyMap.DestinationProperty.Name))
                {
                    property = IfThen(NotEqual(_initialDestination, Constant(null)), property);
                }
                actions.Add(property);
            }
            foreach(var beforeMapAction in _typeMap.BeforeMapActions)
            {
                actions.Insert(0, beforeMapAction.ReplaceParameters(_source, _destination, _context));
            }
            actions.Insert(0, destinationFunc);
            if(_typeMap.MaxDepth > 0)
            {
                actions.Insert(0, Call(_context, ((MethodCallExpression)IncTypeDepthInfo.Body).Method, Constant(_typeMap.Types)));
            }
            actions.AddRange(
                _typeMap.AfterMapActions.Select(
                    afterMapAction => afterMapAction.ReplaceParameters(_source, _destination, _context)));

            if(_typeMap.MaxDepth > 0)
            {
                actions.Add(Call(_context, ((MethodCallExpression)DecTypeDepthInfo.Body).Method, Constant(_typeMap.Types)));
            }

            actions.Add(_destination);

            return Block(actions);
        }

        private Expression CreateMapperFunc(Expression assignmentFunc)
        {
            var mapperFunc = assignmentFunc;

            if(_typeMap.Condition != null)
            {
                mapperFunc =
                    Condition(_typeMap.Condition.Body,
                        mapperFunc, Default(_typeMap.DestinationTypeToUse));
                //mapperFunc = (source, context, destFunc) => _condition(context) ? inner(source, context, destFunc) : default(TDestination);
            }

            if(_typeMap.MaxDepth > 0)
            {
                mapperFunc = Condition(
                    LessThanOrEqual(
                        Call(_context, ((MethodCallExpression)GetTypeDepthInfo.Body).Method, Constant(_typeMap.Types)),
                        Constant(_typeMap.MaxDepth)
                    ),
                    mapperFunc,
                    Default(_typeMap.DestinationTypeToUse));
                //mapperFunc = (source, context, destFunc) => context.GetTypeDepth(types) <= maxDepth ? inner(source, context, destFunc) : default(TDestination);
            }

            if(_typeMap.Profile.AllowNullDestinationValues && _typeMap.SourceType.IsClass())
            {
                mapperFunc =
                    Condition(Equal(_source, Default(_typeMap.SourceType)),
                        Default(_typeMap.DestinationTypeToUse), mapperFunc.RemoveIfNotNull(_source));
                //mapperFunc = (source, context, destFunc) => source == default(TSource) ? default(TDestination) : inner(source, context, destFunc);
            }

            if(_typeMap.PreserveReferences)
            {
                var cache = Variable(_typeMap.DestinationTypeToUse, "cachedDestination");

                var condition = Condition(
                    AndAlso(
                        NotEqual(_source, Constant(null)),
                        AndAlso(
                            Equal(_initialDestination, Constant(null)),
                            Call(Property(_context, "InstanceCache"),
                                typeof(Dictionary<object, object>).GetDeclaredMethod("ContainsKey"), _source)
                            )),
                    Assign(cache,
                        ToType(Property(Property(_context, "InstanceCache"), "Item", _source), _typeMap.DestinationTypeToUse)),
                    Assign(cache, mapperFunc)
                    );

                mapperFunc = Block(new[] { cache }, condition, cache);
            }
            return mapperFunc;
        }

        private Expression CreateNewDestinationFunc(out bool constructorMapping)
        {
            constructorMapping = false;
            if(_typeMap.DestinationCtor != null)
                return _typeMap.DestinationCtor.ReplaceParameters(_source, _context);

            if(_typeMap.ConstructDestinationUsingServiceLocator)
                return Call(MakeMemberAccess(_context, typeof(ResolutionContext).GetDeclaredProperty("Options")),
                    typeof(IMappingOperationOptions).GetDeclaredMethod("CreateInstance")
                        .MakeGenericMethod(_typeMap.DestinationTypeToUse)
                    );

            if(_typeMap.ConstructorMap?.CanResolve == true)
            {
                constructorMapping = true;
                return _typeMap.ConstructorMap.BuildExpression(_configurationProvider, _source, _context);
            }
#if NET45
            if(_typeMap.DestinationTypeToUse.IsInterface())
            {
                var ctor = Call(Constant(ObjectCreator.DelegateFactory), typeof(DelegateFactory).GetDeclaredMethod("CreateCtor", new[] { typeof(Type) }), Call(New(typeof(ProxyGenerator)), typeof(ProxyGenerator).GetDeclaredMethod("GetProxyType"), Constant(_typeMap.DestinationTypeToUse)));
                // We're invoking a delegate here
                return Invoke(ctor);
            }
#endif

            if(_typeMap.DestinationTypeToUse.IsAbstract())
                return Constant(null);

            if(_typeMap.DestinationTypeToUse.IsGenericTypeDefinition())
                return Constant(null);

            return DelegateFactory.GenerateConstructorExpression(_typeMap.DestinationTypeToUse);
        }

        private Expression TryPropertyMap(PropertyMap propertyMap)
        {
            var pmExpression = CreatePropertyMapFunc(propertyMap);

            if(pmExpression == null)
                return null;

            var exception = Parameter(typeof(Exception), "ex");

            var mappingExceptionCtor = ((NewExpression)CtorExpression.Body).Constructor;

            return TryCatch(Block(typeof(void), pmExpression),
                MakeCatchBlock(typeof(Exception), exception,
                    Throw(New(mappingExceptionCtor, Constant("Error mapping types."), exception, Constant(propertyMap.TypeMap.Types), Constant(propertyMap.TypeMap), Constant(propertyMap))), null));
        }

        private Expression CreatePropertyMapFunc(PropertyMap propertyMap)
        {
            var destMember = MakeMemberAccess(_destination, propertyMap.DestinationProperty);

            Expression getter;

            var pi = propertyMap.DestinationProperty as PropertyInfo;
            if(pi != null && pi.GetGetMethod(true) == null)
            {
                getter = Default(propertyMap.DestinationPropertyType);
            }
            else
            {
                getter = destMember;
            }

            Expression destValueExpr;
            if(propertyMap.UseDestinationValue)
            {
                destValueExpr = getter;
            }
            else
            {
                if(_initialDestination.Type.IsValueType())
                {
                    destValueExpr = Default(propertyMap.DestinationPropertyType);
                }
                else
                {
                    destValueExpr = Condition(Equal(_initialDestination, Constant(null)), Default(propertyMap.DestinationPropertyType), getter);
                }
            }

            var valueResolverExpr = BuildValueResolverFunc(propertyMap, getter);

            if(propertyMap.DestinationPropertyType != null)
            {
                var typePair = new TypePair(valueResolverExpr.Type, propertyMap.DestinationPropertyType);
                var typeMap = _configurationProvider.ResolveTypeMap(typePair);
                var match = _configurationProvider.GetMappers().FirstOrDefault(m => m.IsMatch(typePair));
                if(typeMap != null && !typeMap.HasDerivedTypesToInclude())
                {
                    typeMap.Seal(_typeMapRegistry, _configurationProvider);
                    if(typeMap.MapExpression != null)
                    {
                        valueResolverExpr = typeMap.MapExpression.ConvertReplaceParameters(valueResolverExpr, destValueExpr, _context);
                    }
                    else
                    {
                        valueResolverExpr = SetMap(propertyMap, valueResolverExpr, destValueExpr);
                    }
                }
                else if(match != null && typeMap == null)
                {
                    valueResolverExpr = match.MapExpression(_typeMapRegistry, _configurationProvider,
                        propertyMap, valueResolverExpr, destValueExpr,
                        _context);
                }
                else
                {
                    valueResolverExpr = SetMap(propertyMap, valueResolverExpr, destValueExpr);
                }
            }
            else
            {
                valueResolverExpr = SetMap(propertyMap, valueResolverExpr, destValueExpr);
            }

            Expression mapperExpr;
            if(propertyMap.DestinationProperty is FieldInfo)
            {
                mapperExpr = propertyMap.SourceType != propertyMap.DestinationPropertyType
                    ? Assign(destMember, ToType(valueResolverExpr, propertyMap.DestinationPropertyType))
                    : Assign(getter, valueResolverExpr);
            }
            else
            {
                var setter = ((PropertyInfo)propertyMap.DestinationProperty).GetSetMethod(true);
                if(setter == null)
                {
                    mapperExpr = valueResolverExpr;
                }
                else
                {
                    mapperExpr = Assign(destMember, propertyMap.SourceType != propertyMap.DestinationPropertyType
                        ? ToType(valueResolverExpr, propertyMap.DestinationPropertyType)
                        : valueResolverExpr);
                }
            }

            if(propertyMap.PreCondition != null)
            {
                mapperExpr = IfThen(
                    propertyMap.PreCondition.ConvertReplaceParameters(_source, _context),
                    mapperExpr
                    );
            }
            if(propertyMap.Condition != null)
            {
                mapperExpr = IfThen(
                    propertyMap.Condition.ConvertReplaceParameters(
                        _source,
                        _destination,
                        ToType(valueResolverExpr, propertyMap.Condition.Parameters[2].Type),
                        ToType(getter, propertyMap.Condition.Parameters[2].Type),
                        _context
                        ),
                    mapperExpr
                    );
            }

            return mapperExpr;
        }

        private Expression SetMap(PropertyMap propertyMap, Expression valueResolverExpr, Expression destValueExpr)
        {
            return ContextMap(valueResolverExpr, destValueExpr, _context, propertyMap.DestinationPropertyType);
        }

        private Expression BuildValueResolverFunc(PropertyMap propertyMap, Expression destValueExpr)
        {
            Expression valueResolverFunc;
            var valueResolverConfig = propertyMap.ValueResolverConfig;
            var typeMap = propertyMap.TypeMap;

            if(valueResolverConfig != null)
            {
                Expression ctor;
                Type resolverType;
                if(valueResolverConfig.Instance != null)
                {
                    ctor = Constant(valueResolverConfig.Instance);
                    resolverType = valueResolverConfig.Instance.GetType();
                }
                else
                {
                    ctor = Call(MakeMemberAccess(_context, typeof(ResolutionContext).GetDeclaredProperty("Options")),
                        typeof(IMappingOperationOptions).GetDeclaredMethod("CreateInstance")
                            .MakeGenericMethod(valueResolverConfig.Type)
                        );
                    resolverType = valueResolverConfig.Type;
                }

                if(valueResolverConfig.SourceMember != null)
                {
                    var sourceMember = valueResolverConfig.SourceMember.ReplaceParameters(_source);

                    var iResolverType =
                        resolverType.GetTypeInfo()
                            .ImplementedInterfaces.First(t => t.ImplementsGenericInterface(typeof(IMemberValueResolver<,,,>)));

                    var sourceResolverParam = iResolverType.GetGenericArguments()[0];
                    var destResolverParam = iResolverType.GetGenericArguments()[1];
                    var sourceMemberResolverParam = iResolverType.GetGenericArguments()[2];
                    var destMemberResolverParam = iResolverType.GetGenericArguments()[3];

                    valueResolverFunc =
                        ToType(Call(ToType(ctor, resolverType), resolverType.GetDeclaredMethod("Resolve"),
                            ToType(_source, sourceResolverParam),
                            ToType(_destination, destResolverParam),
                            ToType(sourceMember, sourceMemberResolverParam),
                            ToType(destValueExpr, destMemberResolverParam),
                            _context),
                            propertyMap.DestinationPropertyType);
                }
                else if(valueResolverConfig.SourceMemberName != null)
                {
                    var sourceMember = MakeMemberAccess(_source,
                        typeMap.SourceType.GetFieldOrProperty(valueResolverConfig.SourceMemberName));

                    var iResolverType =
                        resolverType.GetTypeInfo()
                            .ImplementedInterfaces.First(t => t.ImplementsGenericInterface(typeof(IMemberValueResolver<,,,>)));

                    var sourceResolverParam = iResolverType.GetGenericArguments()[0];
                    var destResolverParam = iResolverType.GetGenericArguments()[1];
                    var sourceMemberResolverParam = iResolverType.GetGenericArguments()[2];
                    var destMemberResolverParam = iResolverType.GetGenericArguments()[3];

                    valueResolverFunc =
                        ToType(Call(ToType(ctor, resolverType), resolverType.GetDeclaredMethod("Resolve"),
                            ToType(_source, sourceResolverParam),
                            ToType(_destination, destResolverParam),
                            ToType(sourceMember, sourceMemberResolverParam),
                            ToType(destValueExpr, destMemberResolverParam),
                            _context),
                            propertyMap.DestinationPropertyType);
                }
                else
                {
                    var iResolverType = resolverType.GetTypeInfo()
                            .ImplementedInterfaces.First(t => t.IsGenericType() && t.GetGenericTypeDefinition() == typeof(IValueResolver<,,>));

                    var sourceResolverParam = iResolverType.GetGenericArguments()[0];
                    var destResolverParam = iResolverType.GetGenericArguments()[1];
                    var destMemberResolverParam = iResolverType.GetGenericArguments()[2];

                    valueResolverFunc =
                        ToType(Call(ToType(ctor, resolverType), iResolverType.GetDeclaredMethod("Resolve"),
                            ToType(_source, sourceResolverParam),
                            ToType(_destination, destResolverParam),
                            ToType(destValueExpr, destMemberResolverParam),
                            _context),
                            propertyMap.DestinationPropertyType);
                }

            }
            else if(propertyMap.CustomResolver != null)
            {
                valueResolverFunc = propertyMap.CustomResolver.ReplaceParameters(_source, _destination, destValueExpr, _context);
            }
            else if(propertyMap.CustomExpression != null)
            {
                var nullCheckedExpression = propertyMap.CustomExpression.ReplaceParameters(_source).IfNotNull(propertyMap.DestinationPropertyType);
                var returnType = propertyMap.DestinationPropertyType.IsNullableType() && propertyMap.DestinationPropertyType.GetTypeOfNullable() == nullCheckedExpression.Type
                    ? propertyMap.DestinationPropertyType
                    : nullCheckedExpression.Type;
                valueResolverFunc = nullCheckedExpression.Type.IsValueType() && !propertyMap.DestinationPropertyType.IsNullableType()
                    ? nullCheckedExpression
                    : TryCatch(ToType(nullCheckedExpression, returnType),
                        Catch(typeof(NullReferenceException), Default(returnType)),
                        Catch(typeof(ArgumentNullException), Default(returnType))
                        );
            }
            else if(propertyMap.SourceMembers.Any()
                     && propertyMap.SourceType != null
                )
            {
                var last = propertyMap.SourceMembers.Last();
                var pi = last as PropertyInfo;
                if(pi != null && pi.GetGetMethod(true) == null)
                {
                    valueResolverFunc = Default(last.GetMemberType());
                }
                else
                {
                    valueResolverFunc = propertyMap.SourceMembers.Aggregate(
                        (Expression)_source,
                        (inner, getter) => getter is MethodInfo
                            ? getter.IsStatic()
                                ? Call(null, (MethodInfo)getter, inner)
                                : (Expression)Call(inner, (MethodInfo)getter)
                            : MakeMemberAccess(getter.IsStatic() ? null : inner, getter)
                        );
                    valueResolverFunc = valueResolverFunc.IfNotNull(propertyMap.DestinationPropertyType);
                }
            }
            else if(propertyMap.SourceMember != null)
            {
                valueResolverFunc = MakeMemberAccess(_source, propertyMap.SourceMember);
            }
            else
            {
                valueResolverFunc = Throw(Constant(new Exception("I done blowed up")));
            }

            if(propertyMap.DestinationPropertyType == typeof(string) && valueResolverFunc.Type != typeof(string)
                &&
                _configurationProvider.ResolveTypeMap(valueResolverFunc.Type, propertyMap.DestinationPropertyType) == null)
            {
                valueResolverFunc = Call(valueResolverFunc, typeof(object).GetDeclaredMethod("ToString", new Type[0]));
            }

            if(propertyMap.NullSubstitute != null)
            {
                Expression value = Constant(propertyMap.NullSubstitute);
                if(propertyMap.NullSubstitute.GetType() != propertyMap.DestinationPropertyType)
                    value = ToType(value, propertyMap.DestinationPropertyType);
                valueResolverFunc = MakeBinary(ExpressionType.Coalesce, valueResolverFunc, value);
            }
            else if(!typeMap.Profile.AllowNullDestinationValues)
            {
                var toCreate = propertyMap.SourceType ?? propertyMap.DestinationPropertyType;
                if(!toCreate.GetTypeInfo().IsValueType)
                {
                    valueResolverFunc = MakeBinary(ExpressionType.Coalesce,
                        valueResolverFunc,
                        ToType(Call(
                            typeof(ObjectCreator).GetDeclaredMethod("CreateNonNullValue"),
                            Constant(toCreate)
                            ), propertyMap.SourceType));
                }
            }

            return valueResolverFunc;
        }

        public static Expression ContextMap(Expression valueResolverExpr, Expression destValueExpr, ParameterExpression context, Type destinationType)
        {
            var mapMethod = typeof(ResolutionContext).GetDeclaredMethods().First(m => m.Name == "Map").MakeGenericMethod(valueResolverExpr.Type, destinationType);
            var second = Call(
                context,
                mapMethod,
                valueResolverExpr,
                destValueExpr
                );
            return second;
        }

    }
}
